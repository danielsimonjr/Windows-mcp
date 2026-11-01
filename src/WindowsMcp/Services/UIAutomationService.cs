// Element cache is bounded (LRU, 10k entries) to prevent unbounded memory growth on long sessions.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using Windows.Win32;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Services;

public sealed class UIAutomationService : IUIAutomationService
{
    private const int MaxElementCacheEntries = 10_000;

    private readonly UIA3Automation _automation;
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Thread _staThread;
    private readonly Dictionary<string, AutomationElement> _elementCache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _cacheNodes = new();
    private readonly Lock _cacheLock = new();
    private int _nextId;
    private int _disposed;   // 0 = alive, 1 = disposed; treat atomically via Interlocked

    private readonly IInputService _input;
    private readonly IWindowService _windows;
    private readonly UiTreeOptions _treeOptions;
    private readonly ILogger _log;

    /// <param name="input">
    /// Physical input for the two <c>interact_element</c> paths that have no UIA pattern to use:
    /// a click at the element's centre, and keyboard entry when there is no writable ValuePattern.
    /// Also the cursor position in a snapshot's header (A-11).
    /// </param>
    /// <param name="windows">
    /// A-1's inventory: the snapshot's window list, the active window, the monitor inventory the
    /// cursor's display index is resolved against, and the roots the walk starts from.
    /// </param>
    /// <param name="treeOptions">
    /// A-4 (roadmap C7): the process-level element budget, from <c>--max-tree-elements</c>. Null
    /// means <see cref="UiTreeOptions.Default"/>, so a test can construct the service directly.
    /// </param>
    /// <param name="log">
    /// Optional so tests can construct the service directly. The find path swallows per-element and
    /// per-window failures by design (D-5); this is where a failure that is <i>not</i> a recognised
    /// stale-element failure gets recorded, so a new failure mode is visible instead of silent.
    /// </param>
    /// <param name="windows">The A-1 inventory: the snapshot's header and the roots it walks.</param>
    /// <param name="treeOptions">The element budget in force when a call does not name its own (A-4); null = 500.</param>
    public UIAutomationService(IInputService input, IWindowService windows,
        UiTreeOptions? treeOptions = null, ILogger<UIAutomationService>? log = null)
    {
        _input = input;
        _windows = windows;
        _treeOptions = treeOptions ?? UiTreeOptions.Default;
        _log = log ?? (ILogger)NullLogger<UIAutomationService>.Instance;
        _automation = new UIA3Automation();
        _staThread = new Thread(WorkerLoop) { IsBackground = true, Name = "WindowsMcp-UA-STA" };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void WorkerLoop()
    {
        foreach (var work in _workQueue.GetConsumingEnumerable())
        {
            try { work(); } catch { /* exceptions are propagated via TaskCompletionSource in each work item */ }
        }
    }

    private Task<T> OnStaAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(UIAutomationService));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register cancellation: if ct fires while work is still backlogged in the queue,
        // we don't want the caller's await to sit forever.
        var ctRegistration = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            _workQueue.Add(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                    tcs.TrySetResult(work());
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException)   // CompleteAdding raced with us
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(UIAutomationService)));
        }

        // Dispose registration when task completes — prevents leak if ct outlives task.
        tcs.Task.ContinueWith(_ => ctRegistration.Dispose(), TaskScheduler.Default);
        return tcs.Task;
    }

    public Task<ElementTree> GetStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            // A-4: the same depth-3 shape, now bounded. The root reports the truncation, nothing else changes.
            var budget = new ElementBudget(_treeOptions.MaxElements);
            var tree = BuildTree(GetForegroundRoot(), depth: 3, budget)
                ?? new ElementTree(ToInfo(GetForegroundRoot()), Array.Empty<ElementTree>());
            return budget.Truncated ? tree with { Truncated = true, ElementLimit = budget.Limit } : tree;
        }, ct);
    }

    /// <summary>
    /// The element whose subtree represents "current state": the foreground top-level window
    /// (what an agent actually acts on). Falls back to the focused element, then the desktop.
    /// Rooting at the focused element directly is wrong — a focused leaf control (a text box,
    /// a button) has no children, yielding an empty, useless tree.
    /// </summary>
    private static unsafe nint HwndPointer(Windows.Win32.Foundation.HWND h) => (nint)h.Value;

    private AutomationElement GetForegroundRoot()
    {
        try
        {
            var hwnd = PInvoke.GetForegroundWindow();
            if (!hwnd.IsNull)
            {
                var window = _automation.FromHandle(HwndPointer(hwnd));
                if (window is not null) return window;
            }
        }
        catch { /* fall through to focused element / desktop */ }

        return _automation.FocusedElement() ?? _automation.GetDesktop();
    }

    /// <summary>Null when the budget refused this node; a refused child ends the parent's child list.</summary>
    private ElementTree? BuildTree(AutomationElement el, int depth, ElementBudget budget)
    {
        if (!budget.TryTake()) return null;
        var info = ToInfo(el);
        if (depth <= 0) return new ElementTree(info, Array.Empty<ElementTree>());
        var children = new List<ElementTree>();
        foreach (var c in el.FindAllChildren())
        {
            var child = BuildTree(c, depth - 1, budget);
            if (child is null) break;
            children.Add(child);
        }
        return new ElementTree(info, children.ToArray());
    }

    private ElementInfo ToInfo(AutomationElement el)
    {
        string id;
        lock (_cacheLock)
        {
            id = $"el_{_nextId++}";
            _elementCache[id] = el;
            var node = _cacheOrder.AddLast(id);
            _cacheNodes[id] = node;
            EvictOldestIfNeeded();
        }
        return new ElementInfo(
            ElementId: id,
            Name: TryGetName(el),
            ControlType: TryGetControlType(el),
            IsEnabled: TryGetIsEnabled(el),
            IsOffscreen: TryGetIsOffscreen(el),
            Bounds: TryGetBounds(el),
            Value: TryGetValue(el),
            IsChecked: TryGetChecked(el),
            IsSelected: TryGetSelected(el));
    }

    // A-13: every UI-supplied string goes through UiText.Sanitize before it reaches a DTO.
    private static string TryGetName(AutomationElement el)
    {
        try { return UiText.Sanitize(el.Name); } catch { return ""; }
    }

    private static string TryGetControlType(AutomationElement el)
    {
        try { return el.ControlType.ToString(); } catch { return "Unknown"; }
    }

    private static bool TryGetIsEnabled(AutomationElement el)
    {
        try { return el.IsEnabled; } catch { return false; }
    }

    private static bool TryGetIsOffscreen(AutomationElement el)
    {
        try { return el.IsOffscreen; } catch { return false; }
    }

    // The last unguarded read in ToInfo before D-5: a bounds read on an element that died between
    // the walk and the read used to fail the whole find call.
    private static Bounds? TryGetBounds(AutomationElement el)
    {
        try
        {
            var b = el.BoundingRectangle;
            return new Bounds((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
        }
        catch { return null; }
    }

    private static bool TryIsScrollable(AutomationElement el)
    {
        try { return el.Patterns.Scroll.IsSupported; } catch { return false; }
    }

    private static string? TryGetValue(AutomationElement el)
    {
        try
        {
            var raw = el.Patterns.Value.PatternOrDefault?.Value.Value;
            return raw is null ? null : UiText.Sanitize(raw);
        }
        catch { return null; }
    }

    private static bool? TryGetChecked(AutomationElement el)
    {
        try { return el.Patterns.Toggle.PatternOrDefault?.ToggleState.Value == ToggleState.On; } catch { return null; }
    }

    private static bool? TryGetSelected(AutomationElement el)
    {
        try { return el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.Value; } catch { return null; }
    }

    /// <summary>Result cap. Applied AFTER every filter (D-7) so an on-screen match is never
    /// crowded out by off-screen ones. A real element budget on the walk itself is A-4.</summary>
    private const int MaxMatches = 20;

    /// <summary>
    /// D-6's interactive set, now owned by <see cref="UiClassifier"/> (A-2) so the find path and
    /// the snapshot classify from one list. Same instance, so the two cannot drift.
    /// </summary>
    internal static ControlType[] InteractiveControlTypes => UiClassifier.InteractiveControlTypes;

    /// <summary>Ids the previous snapshot issued; evicted when the next one starts (roadmap C5).</summary>
    private readonly List<string> _snapshotIds = new();

    /// <summary>
    /// A-2: one call for the whole desktop. Header from the A-1 inventory and the cursor, then a
    /// budgeted (A-4) walk of every non-minimised window (or the foreground / a named one),
    /// classified into interactive elements with centre coordinates and action hints, and
    /// scrollable regions with their percentages (A-3). Ids are valid until the next snapshot.
    /// </summary>
    public async Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed != 0) throw new ObjectDisposedException(nameof(UIAutomationService));
        if (request.Scope == SnapshotScope.Window && string.IsNullOrWhiteSpace(request.WindowTitle))
            throw new ArgumentException("scope=window requires windowTitle: the title of the window to snapshot.", nameof(request.WindowTitle));
        if (request.Scope != SnapshotScope.Window && request.WindowTitle is not null)
            throw new ArgumentException("windowTitle is only used with scope=window.", nameof(request.WindowTitle));
        if (request.MaxElements < 0)
            throw new ArgumentException($"maxElements must be 0 (the server default) or positive, got {request.MaxElements}", nameof(request.MaxElements));

        var sw = Stopwatch.StartNew();
        var limit = request.MaxElements > 0 ? request.MaxElements : _treeOptions.MaxElements;
        long headerMs = 0;

        // Header — each collaborator once; the list is reused for the roots.
        var cursor = await _input.GetCursorPositionAsync(ct);
        var monitors = await _windows.EnumerateMonitorsAsync(ct);
        var windows = await _windows.ListAsync(true, false, ct);
        var active = WindowFilter.ActiveOf(windows);
        var cursorMonitor = CursorMath.MonitorIndexOf(cursor.X, cursor.Y, monitors);
        headerMs = sw.ElapsedMilliseconds;

        var targets = request.Scope switch
        {
            SnapshotScope.Desktop => windows.Where(w => w.State != WindowState.Minimized).ToArray(),
            SnapshotScope.Foreground => active is null ? Array.Empty<WindowInfo>() : [active],
            _ => MatchWindows(windows, request.WindowTitle!),
        };

        var walked = await OnStaAsync(() =>
        {
            var budget = new ElementBudget(limit);
            var traverser = new UiTraverser(_automation);
            // Dom: None = a normal walk; Page = walked from the RootWebArea document (entry 0 is the
            // page); NoPage = a browser window with no page document, walked whole (A-5).
            var perWindow = new List<(string Title, IReadOnlyList<UiWalkEntry> Entries, DomState Dom)>();

            if (request.Scope == SnapshotScope.Foreground && active is null)
            {
                // No inventory entry is flagged active (the desktop, a cloaked window): fall back
                // to whatever UIA says is in front, as get_state does. No inventory row means no
                // IsBrowser either, so DOM mode does not apply here.
                var root = GetForegroundRoot();
                perWindow.Add((TryGetName(root), traverser.Walk(root, TryGetName(root), budget), DomState.None));
            }

            foreach (var w in targets)
            {
                if (budget.Truncated) break;
                if (w.Hwnd == 0) continue;
                try
                {
                    var root = _automation.FromHandle((nint)w.Hwnd)
                        ?? throw new InvalidOperationException("no automation element for the window handle");
                    var dom = DomState.None;
                    if (request.UseDom && w.IsBrowser)
                    {
                        // A-5: walk the page, not the browser — the address bar and tab strip are
                        // never visited because the walk starts below them.
                        var document = FindPageDocument(root);
                        if (document is null) dom = DomState.NoPage;
                        else { root = document; dom = DomState.Page; }
                    }
                    perWindow.Add((w.Title, traverser.Walk(root, w.Title, budget), dom));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSkipped($"window '{w.Title}'", ex);
                }
            }

            // Ids: evict what the previous snapshot issued, then issue one per walked node so the
            // tree, the interactive list and get_element all agree.
            var interactive = new List<SnapshotElement>();
            var scrollable = new List<SnapshotScrollable>();
            var trees = new List<ElementTree>();
            var pages = new List<SnapshotPage>();
            lock (_cacheLock)
            {
                foreach (var old in _snapshotIds) _elementCache.Remove(old);
                _snapshotIds.Clear();

                foreach (var (title, entries, dom) in perWindow)
                {
                    var ids = new string[entries.Count];
                    for (int i = 0; i < entries.Count; i++)
                    {
                        ids[i] = $"el_{_nextId++}";
                        _elementCache[ids[i]] = entries[i].Element;
                        _snapshotIds.Add(ids[i]);
                        var (element, region) = Project(entries[i].Node, ids[i]);
                        // A-5 correction 1: the page document keeps its id and its scroll row but is not a control.
                        if (dom == DomState.Page && DomCorrection.SuppressesInteractive(entries[i].Node, entries[i].ParentIndex))
                            element = null;
                        if (element is not null) interactive.Add(element);
                        if (region is not null) scrollable.Add(region);
                    }
                    if (request.IncludeTree && entries.Count > 0)
                        trees.Add(ToTree(entries, ids));
                    if (dom == DomState.Page && entries.Count > 0)
                        pages.Add(DomCorrection.PageFor(ids[0], entries.Select(e => (e.Node, e.ParentIndex)).ToList()));
                    else if (dom == DomState.NoPage)
                        pages.Add(DomCorrection.NoPage(title));
                }
            }

            ElementTree? tree = request.IncludeTree
                ? new ElementTree(new ElementInfo("desktop", "", "Desktop", true, false, null, null, null, null), trees.ToArray())
                : null;
            return (Interactive: interactive.ToArray(), Scrollable: scrollable.ToArray(), Tree: tree,
                    Count: budget.Count, budget.Truncated,
                    Pages: request.UseDom ? pages.ToArray() : null);
        }, ct);

        StageTiming[]? stages = null;
        if (_treeOptions.Profile)
        {
            stages = [new StageTiming("header", headerMs), new StageTiming("walk", Math.Max(0, sw.ElapsedMilliseconds - headerMs))];
            _log.LogInformation("snapshot: header {HeaderMs} ms, walk {WalkMs} ms, {Count} elements ({Interactive} interactive)",
                stages[0].Ms, stages[1].Ms, walked.Count, walked.Interactive.Length);
        }

        return new SnapshotResult(
            Windows: windows,
            ActiveWindow: active,
            Cursor: cursor,
            CursorMonitorIndex: cursorMonitor,
            Interactive: walked.Interactive,
            Scrollable: walked.Scrollable,
            Tree: walked.Tree,
            Truncated: walked.Truncated,
            ElementLimit: limit,
            ElementCount: walked.Count,
            CaptureMs: sw.ElapsedMilliseconds,
            Stages: stages,
            Pages: walked.Pages);
    }

    /// <summary>How one window was walked for a snapshot (A-5).</summary>
    private enum DomState { None, Page, NoPage }

    /// <summary>scope=window against the inventory: exact title first, then substring, case-insensitive; none → name what is open.</summary>
    private static WindowInfo[] MatchWindows(WindowInfo[] windows, string title)
    {
        var named = windows.Where(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 0)
            named = windows.Where(w => w.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 0)
        {
            var open = windows.Select(w => w.Title).Where(t => t.Length > 0).Distinct().Take(15).ToArray();
            throw new KeyNotFoundException(
                $"No top-level window matching '{title}'. Open windows: " +
                (open.Length > 0 ? string.Join(", ", open.Select(n => $"'{n}'")) : "(none with a title)"));
        }
        return named;
    }

    /// <summary>
    /// A-5 phase 1: the web page under a browser window — the first descendant that is a
    /// <see cref="ControlType.Document"/> whose AutomationId is <c>RootWebArea</c> — or null when
    /// there is none (a page still loading, Firefox, a non-web window). Chromium builds its UIA
    /// tree lazily on the first query, so the first find can come back empty on a page that is
    /// there: the search is retried a bounded number of times before it concludes there is no page.
    /// </summary>
    internal static AutomationElement? FindPageDocument(AutomationElement root, int attempts = 3, int pauseMs = 150)
    {
        var cf = root.ConditionFactory;
        var page = cf.ByControlType(ControlType.Document).And(cf.ByAutomationId("RootWebArea"));
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var found = root.FindFirstDescendant(page);
                if (found is not null) return found;
            }
            catch { /* the window went away or refused: the same as no page */ }
            if (attempt + 1 == attempts) break;
            // Chromium switches its accessibility tree on for the first client that asks and fills
            // it in after answering; a plain Document query is the nudge, the pause the fill-in time.
            try { root.FindFirstDescendant(cf.ByControlType(ControlType.Document)); } catch { }
            Thread.Sleep(pauseMs);
        }
        return null;
    }

    /// <summary>
    /// One walked node → its place in the lists: an interactive element (never carrying a
    /// password's value), a scrollable region, both, or neither. Pure, so the password rule and
    /// the split are testable without a desktop.
    /// </summary>
    internal static (SnapshotElement? Interactive, SnapshotScrollable? Scrollable) Project(UiNode n, string id)
    {
        if (n.Bounds is not { } b) return (null, null);
        var (cx, cy) = UiClassifier.CenterOf(b);
        SnapshotElement? element = null;
        if (UiClassifier.Classify(n) == UiRole.Interactive)
        {
            element = new SnapshotElement(
                ElementId: id, Window: n.Window, ControlType: n.ControlType, Name: n.Name,
                CenterX: cx, CenterY: cy, Bounds: b, Action: UiClassifier.ActionFor(n),
                Focused: n.HasFocus, IsPassword: n.IsPassword, Value: n.IsPassword ? null : n.Value,
                Toggle: n.ToggleState, Expand: n.ExpandState, Shortcut: UiClassifier.ShortcutOf(n),
                RangeValue: n.RangeValue, RangeMin: n.RangeMin, RangeMax: n.RangeMax);
        }
        SnapshotScrollable? region = UiClassifier.IsScrollable(n)
            ? new SnapshotScrollable(id, n.Window, n.ControlType, n.Name, cx, cy, b, n.Scroll!)
            : null;
        return (element, region);
    }

    /// <summary>Rebuilds the walk's pre-order entries into an ElementTree, ids matching the lists.</summary>
    private static ElementTree ToTree(IReadOnlyList<UiWalkEntry> entries, string[] ids)
    {
        var children = new List<int>[entries.Count];
        for (int i = 0; i < entries.Count; i++) children[i] = new List<int>();
        for (int i = 1; i < entries.Count; i++)
            if (entries[i].ParentIndex >= 0) children[entries[i].ParentIndex].Add(i);

        ElementTree Build(int i)
        {
            var n = entries[i].Node;
            var info = new ElementInfo(ids[i], n.Name, n.ControlType, n.IsEnabled, n.IsOffscreen, n.Bounds, n.Value,
                n.ToggleState is null ? null : n.ToggleState == "On", null, n.Scroll);
            return new ElementTree(info, children[i].Select(Build).ToArray());
        }
        return Build(0);
    }

    public Task<FindElementResult> FindElementAsync(string text, FindKind kind = FindKind.Any,
        FindScope scope = FindScope.Foreground, string? windowTitle = null,
        bool includeOffscreen = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (scope == FindScope.Window && string.IsNullOrWhiteSpace(windowTitle))
            throw new ArgumentException("scope=window requires windowTitle: the title of the window to search.", nameof(windowTitle));
        if (scope != FindScope.Window && windowTitle is not null)
            throw new ArgumentException("windowTitle is only used with scope=window.", nameof(windowTitle));

        return OnStaAsync(() =>
        {
            var hits = new List<ElementInfo>(MaxMatches);
            foreach (var root in RootsFor(scope, windowTitle))
            {
                if (hits.Count >= MaxMatches) break;
                CollectFrom(root, text, kind, includeOffscreen, hits);
            }
            return new FindElementResult(hits.ToArray());
        }, ct);
    }

    /// <summary>
    /// The window root(s) a find walks. One root per window, so a window closing mid-walk drops
    /// that window from the results instead of failing the call (D-5).
    /// </summary>
    private IEnumerable<AutomationElement> RootsFor(FindScope scope, string? windowTitle)
    {
        if (scope == FindScope.Foreground)
        {
            yield return GetForegroundRoot();
            yield break;
        }

        AutomationElement[] windows;
        try { windows = _automation.GetDesktop().FindAllChildren(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSkipped("the desktop's window list", ex);
            yield break;
        }

        if (scope == FindScope.Desktop)
        {
            foreach (var w in windows) yield return w;
            yield break;
        }

        // FindScope.Window. Matched on the window's own UIA name rather than through WindowService,
        // whose FindWindow(null, title) is an exact WHOLE-title match: titles carry volatile
        // decoration ("Untitled - Notepad" gains a leading '*' after one keystroke). B-10's fuzzy
        // matcher replaces the substring step here when it lands. Several windows can legitimately
        // share a title (two Explorer windows) — search all of them rather than refusing.
        var named = windows.Where(w => string.Equals(TryGetName(w), windowTitle, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 0)
            named = windows.Where(w => TryGetName(w).Contains(windowTitle!, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 0)
        {
            // No tool returns a window inventory yet (A-1), so an agent that guessed wrong cannot
            // otherwise discover what IS open. Name them.
            var open = windows.Select(TryGetName).Where(n => n.Length > 0).Distinct().Take(15).ToArray();
            throw new KeyNotFoundException(
                $"No top-level window matching '{windowTitle}'. Open windows: " +
                (open.Length > 0 ? string.Join(", ", open.Select(n => $"'{n}'")) : "(none with a title)"));
        }
        foreach (var w in named) yield return w;
    }

    /// <summary>Collects matches from one window root into <paramref name="hits"/>, stopping at
    /// <see cref="MaxMatches"/>. Neither a failed walk nor a failed element aborts the search.</summary>
    private void CollectFrom(AutomationElement root, string text, FindKind kind, bool includeOffscreen, List<ElementInfo> hits)
    {
        // The root itself is a candidate: with a window-scoped search the window element is not one
        // of its own descendants, and find_element("Notepad") should still find the window. The kind
        // test has to be applied CLIENT-side here — the UIA condition below only filters descendants,
        // so without this every window Pane counted as a match for every kind and filled the cap.
        if (RootMatchesKind(root, kind) && TryEvaluate(root, text, kind, includeOffscreen) is { } rootInfo)
            hits.Add(rootInfo);

        AutomationElement[] candidates;
        try
        {
            // Push the control-type filter into the UIA condition so the provider marshals fewer
            // elements. Name stays client-side: UIA property conditions are exact-match and
            // find_element is documented as a "contains" search.
            var condition = KindCondition(_automation.ConditionFactory, kind);
            candidates = condition is null ? root.FindAllDescendants() : root.FindAllDescendants(condition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSkipped($"window '{TryGetName(root)}'", ex);
            return;
        }

        foreach (var el in candidates)
        {
            if (hits.Count >= MaxMatches) return;
            if (TryEvaluate(el, text, kind, includeOffscreen) is { } info) hits.Add(info);
        }
    }

    /// <summary>
    /// The client-side twin of <see cref="KindCondition"/>, for the one element the UIA condition
    /// cannot filter: the walk's own root. Scrollable is decided by pattern in
    /// <see cref="TryEvaluate"/>, so it passes here.
    /// </summary>
    private static bool RootMatchesKind(AutomationElement el, FindKind kind) => kind switch
    {
        FindKind.Text => TryGetControlTypeEnum(el) is ControlType.Text or ControlType.Edit or ControlType.Document,
        FindKind.Interactive => Array.IndexOf(InteractiveControlTypes, TryGetControlTypeEnum(el)) >= 0,
        _ => true,
    };

    private static ControlType TryGetControlTypeEnum(AutomationElement el)
    {
        try { return el.ControlType; } catch { return ControlType.Unknown; }
    }

    private static ConditionBase? KindCondition(ConditionFactory cf, FindKind kind) => kind switch
    {
        FindKind.Text => new OrCondition(
            cf.ByControlType(ControlType.Text), cf.ByControlType(ControlType.Edit), cf.ByControlType(ControlType.Document)),
        FindKind.Interactive => new OrCondition(InteractiveControlTypes.Select(t => (ConditionBase)cf.ByControlType(t))),
        // Any matches everything; Scrollable is a pattern test, not a property, so it stays
        // client-side in TryEvaluate. Both walk unconditioned.
        _ => null,
    };

    /// <summary>
    /// Decides whether one element is a match, and turns it into an <see cref="ElementInfo"/>.
    /// EVERY read here is guarded, and the whole method is wrapped: a desktop always contains
    /// elements that are about to die (a fading tooltip, a closing menu, a virtualised row), and
    /// before D-5 any one of them failed the entire call. Catching broadly is deliberate — a
    /// provider can raise PropertyNotSupportedException or a bare COMException as well as
    /// UIA_E_ELEMENTNOTAVAILABLE, and none of that is worth failing a search over.
    /// </summary>
    private ElementInfo? TryEvaluate(AutomationElement el, string text, FindKind kind, bool includeOffscreen)
    {
        try
        {
            // Cheapest discriminating test first: one Name read, and it cuts hardest when the
            // caller passed text. Visibility costs two reads, so it runs on the survivors.
            if (text.Length > 0 && !TryGetName(el).Contains(text, StringComparison.OrdinalIgnoreCase)) return null;
            if (kind == FindKind.Scrollable && !TryIsScrollable(el)) return null;
            if (!includeOffscreen && !IsVisibleEnough(el)) return null;
            return ToInfo(el);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// D-7: upstream's <c>is_visible = area &gt; 0 and not is_offscreen</c>, with upstream's
    /// <c>Edit</c> exception — Chromium/WebView2 and some XAML providers report IsOffscreen on edit
    /// fields that are merely scrolled in a container but are still the right target for
    /// <c>type</c>. Real bounds are the guard that keeps destroyed controls out.
    /// Note this is NOT a sign test on X/Y: a monitor left of or above the primary has negative
    /// bounds and is perfectly on screen (D-3).
    /// </summary>
    private static bool IsVisibleEnough(AutomationElement el)
    {
        var b = TryGetBounds(el);
        if (b is null || b.Width <= 0 || b.Height <= 0) return false;
        if (!TryGetIsOffscreen(el)) return true;
        return TryGetControlType(el) == nameof(ControlType.Edit);
    }

    private void LogSkipped(string what, Exception ex)
    {
        // A stale element is the expected case and stays quiet; anything else is a failure mode we
        // have not seen, and swallowing it silently is how a real bug hides behind D-5's guards.
        if (IsElementGone(ex)) _log.LogDebug(ex, "find: skipped {What} (no longer available)", what);
        else _log.LogWarning(ex, "find: skipped {What} after an unexpected failure", what);
    }

    public Task<ElementInfo> GetElementAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            AutomationElement el;
            lock (_cacheLock)
            {
                if (!_elementCache.TryGetValue(elementId, out el!))
                    throw new KeyNotFoundException($"Element '{elementId}' not in cache");
            }
            return ToInfo(el);
        }, ct);
    }

    public Task<string> GetTextAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var el = ResolveCached(elementId);
            return UiText.Sanitize(el.Patterns.Value.PatternOrDefault?.Value.Value ?? el.Name);
        }, ct);
    }

    public Task<AssertResult> AssertElementAsync(string elementId, string state, string? expected = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() => AssertOnSta(elementId, state, expected), ct);
    }

    private static readonly string[] AssertStates = ["exists", "enabled", "checked", "value", "visible", "focused"];

    // D-4: every advertised state is implemented, the result says what was observed, and a stale
    // element (its window closed since the id was issued) is a FAIL, never an exception.
    private AssertResult AssertOnSta(string elementId, string state, string? expected)
    {
        var s = state.ToLowerInvariant();
        if (Array.IndexOf(AssertStates, s) < 0)
            throw new ArgumentException($"Unknown assertion state '{state}'; expected exists|enabled|checked|value|visible|focused.", nameof(state));
        if (s == "value" && expected is null)
            throw new ArgumentException("'value' requires expected: the text to compare against.", nameof(expected));
        if (s != "value" && expected is not null)
            throw new ArgumentException("expected is only used with state=value.", nameof(expected));

        AutomationElement? el;
        lock (_cacheLock) _elementCache.TryGetValue(elementId, out el);
        if (el is null)
        {
            // "exists" is the one question where "I don't know that id" is the answer; for every
            // other state an unknown id is a caller bug (ids only come from find_element/get_state).
            if (s == "exists") return new AssertResult(elementId, s, false, "unknown element id");
            throw new KeyNotFoundException($"Element '{elementId}' not in cache");
        }

        AssertResult Result(bool pass, string observed) => new(elementId, s, pass, observed);

        try
        {
            // Liveness probe for every state. Providers that raise UIA_E_ELEMENTNOTAVAILABLE for a
            // destroyed element are handled by the catch below; the Win32 proxy instead keeps
            // answering with defaults (ControlType Pane, empty Name, ProcessId 0), so a ProcessId of
            // 0 is the reliable "gone" signal for a closed HWND.
            if (el.Properties.ProcessId.ValueOrDefault <= 0)
                return Result(false, "element no longer available");

            switch (s)
            {
                case "exists":
                    return Result(true, Describe(el));
                case "enabled":
                    {
                        // Providers may omit optional properties (FlaUI then throws
                        // PropertyNotSupportedException on .Value); use UIA's documented defaults.
                        bool enabled = el.Properties.IsEnabled.TryGetValue(out bool isEnabled) ? isEnabled : true;
                        return Result(enabled, enabled ? "enabled" : "disabled");
                    }
                case "checked":
                    {
                        var toggle = el.Patterns.Toggle.PatternOrDefault;
                        if (toggle is null) return Result(false, $"no TogglePattern on {Describe(el)}");
                        var toggleState = toggle.ToggleState.ValueOrDefault;
                        return Result(toggleState == ToggleState.On, $"toggle state {toggleState}");
                    }
                case "visible":
                    {
                        // IsOffscreen is optional (modern Notepad's document omits it): unsupported
                        // means "not offscreen", and the bounds decide.
                        if (el.Properties.IsOffscreen.ValueOrDefault) return Result(false, "offscreen");
                        var r = el.Properties.BoundingRectangle.ValueOrDefault;
                        if (r.IsEmpty) return Result(false, "empty bounds");
                        return Result(true, $"on screen at ({r.X},{r.Y}) {r.Width}x{r.Height}");
                    }
                case "focused":
                    {
                        if (el.Properties.HasKeyboardFocus.ValueOrDefault) return Result(true, "has keyboard focus");
                        // Some frameworks report focus on the element UIA returns as focused without
                        // setting HasKeyboardFocus on the cached instance; compare identities too.
                        var focused = TryGetFocusedElement();
                        if (focused is not null && SameElement(focused, el)) return Result(true, "has keyboard focus");
                        return Result(false, focused is null ? "nothing has focus" : $"focus is on {Describe(focused)}");
                    }
                case "value":
                    {
                        // Same read as get_text: the ValuePattern value, else the Name.
                        var actual = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
                        var source = "ValuePattern";
                        if (actual is null) { actual = el.Properties.Name.ValueOrDefault ?? ""; source = "Name"; }
                        // A-13: compare and report what find_element/get_text hand the model, not the
                        // raw string — otherwise a value read back from those tools can never match.
                        actual = UiText.Sanitize(actual);
                        return Result(string.Equals(actual, expected, StringComparison.Ordinal), $"value is '{actual}' (from {source})");
                    }
                default:
                    throw new InvalidOperationException($"Unhandled assertion state '{s}'.");
            }
        }
        catch (Exception ex) when (IsElementGone(ex))
        {
            return Result(false, "element no longer available");
        }
    }

    private AutomationElement? TryGetFocusedElement()
    {
        try { return _automation.FocusedElement(); } catch { return null; }
    }

    // CompareElements fails (0x80040201) when either side has gone stale; that is "not the same".
    private static bool SameElement(AutomationElement a, AutomationElement b)
    {
        try { return a.Equals(b); } catch { return false; }
    }

    /// <summary>
    /// True when <paramref name="ex"/> means the element was destroyed after its id was issued:
    /// FlaUI's <see cref="FlaUI.Core.Exceptions.ElementNotAvailableException"/> (its conversion of
    /// UIA_E_ELEMENTNOTAVAILABLE), the raw HRESULT on paths FlaUI does not wrap, or the RPC failures
    /// UIA raises once the owning process has exited. Shared with the find/wait path (checklist D-5).
    /// </summary>
    internal static bool IsElementGone(Exception ex)
    {
        if (ex is FlaUI.Core.Exceptions.ElementNotAvailableException) return true;
        if (ex is not COMException com) return false;
        return com.HResult is
            unchecked((int)0x80040201)   // UIA_E_ELEMENTNOTAVAILABLE
            or unchecked((int)0x80010108)   // RPC_E_DISCONNECTED
            or unchecked((int)0x800706BA)   // RPC_S_SERVER_UNAVAILABLE
            or unchecked((int)0x800706BE);  // RPC_S_CALL_FAILED
    }

    /// <summary>
    /// Outcome of the STA half of <see cref="InteractAsync"/>: the result to report, plus any input
    /// the caller must inject <b>off</b> the STA thread (a blocked SendInput must never stall the UIA
    /// queue). At most one of the pending members is set.
    /// </summary>
    private sealed record InteractStep(InteractResult Result, (int X, int Y)? PendingClick = null, string? PendingText = null);

    public async Task<InteractResult> InteractAsync(string elementId, string action, string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var step = await OnStaAsync(() => InteractOnSta(elementId, action, value), ct).ConfigureAwait(false);

        if (step.PendingClick is { } point)
            await _input.ClickAsync(point.X, point.Y, MouseButton.Left, 1, ct).ConfigureAwait(false);
        else if (step.PendingText is { } text)
            await _input.TypeAsync(text, ct).ConfigureAwait(false);

        return step.Result;
    }

    // D-2: every branch either acts through a UIA pattern or fails naming the pattern and the
    // control — never a silent no-op — and the result says which pattern or fallback fired.
    private InteractStep InteractOnSta(string elementId, string action, string? value)
    {
        var el = ResolveCached(elementId);
        var a = action.ToLowerInvariant();
        InteractResult Done(string method, string? detail = null) => new(elementId, a, method, detail);

        switch (a)
        {
            case "click":
                {
                    if (el.Patterns.Invoke.PatternOrDefault is { } invoke) { invoke.Invoke(); return new(Done("InvokePattern")); }
                    if (el.Patterns.SelectionItem.PatternOrDefault is { } selection) { selection.Select(); return new(Done("SelectionItemPattern")); }
                    if (el.Patterns.Toggle.PatternOrDefault is { } toggle) { toggle.Toggle(); return new(Done("TogglePattern", ToggleDetail(toggle))); }
                    var (x, y) = ClickPoint(el);
                    return new(Done("PhysicalClick", $"({x},{y})"), PendingClick: (x, y));
                }
            case "invoke":
                {
                    var invoke = el.Patterns.Invoke.PatternOrDefault ?? throw NotSupported(el, "InvokePattern");
                    invoke.Invoke();
                    return new(Done("InvokePattern"));
                }
            case "toggle":
                {
                    var toggle = el.Patterns.Toggle.PatternOrDefault ?? throw NotSupported(el, "TogglePattern");
                    toggle.Toggle();
                    return new(Done("TogglePattern", ToggleDetail(toggle)));
                }
            case "select":
                {
                    if (value is null)
                    {
                        var selection = el.Patterns.SelectionItem.PatternOrDefault ?? throw NotSupported(el, "SelectionItemPattern");
                        selection.Select();
                        return new(Done("SelectionItemPattern"));
                    }

                    // With a value the element is a container (combo box, list): open it if it can be
                    // opened, then select the child item by name.
                    el.Patterns.ExpandCollapse.PatternOrDefault?.Expand();
                    var item = el.FindFirstDescendant(cf => cf.ByName(value))
                        ?? throw new KeyNotFoundException($"No item named '{value}' under {Describe(el)}.");
                    if (item.Patterns.SelectionItem.PatternOrDefault is { } itemSelection) { itemSelection.Select(); return new(Done("SelectionItemPattern", $"item '{value}'")); }
                    if (item.Patterns.Invoke.PatternOrDefault is { } itemInvoke) { itemInvoke.Invoke(); return new(Done("InvokePattern", $"item '{value}'")); }
                    throw NotSupported(item, "SelectionItemPattern");
                }
            case "focus":
                el.Focus();
                return new(Done("Focus"));
            case "type":
                {
                    if (value is null) throw new ArgumentException("'type' requires a value: the text to enter.", nameof(value));
                    el.Focus();
                    var valuePattern = el.Patterns.Value.PatternOrDefault;
                    if (valuePattern is not null && !valuePattern.IsReadOnly.ValueOrDefault)
                    {
                        valuePattern.SetValue(value);
                        return new(Done("ValuePattern", "replaced the whole value"));
                    }
                    return new(Done("Keyboard", "typed at the caret"), PendingText: value);
                }
            default:
                throw new ArgumentException($"Unknown interact action '{action}'; expected click|invoke|toggle|select|focus|type.", nameof(action));
        }
    }

    private static (int X, int Y) ClickPoint(AutomationElement el)
    {
        var r = el.BoundingRectangle;
        if (TryGetIsOffscreen(el) || r.IsEmpty)
            throw new InvalidOperationException($"{Describe(el)} supports no Invoke/SelectionItem/Toggle pattern and has no on-screen bounds to click.");
        return (r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private static string? ToggleDetail(FlaUI.Core.Patterns.ITogglePattern toggle)
    {
        try { return $"now {toggle.ToggleState.ValueOrDefault}"; } catch { return null; }
    }

    private static string Describe(AutomationElement el) => $"{TryGetControlType(el)} '{TryGetName(el)}'";

    private static NotSupportedException NotSupported(AutomationElement el, string pattern)
        => new($"{pattern} not supported on {Describe(el)}.");

    public Task<TableData> GetTableAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var el = ResolveCached(elementId);
            var grid = el.Patterns.Grid.PatternOrDefault
                ?? throw new InvalidOperationException("Element doesn't support GridPattern");
            var rows = grid.RowCount.Value;
            var cols = grid.ColumnCount.Value;

            // GridPattern exposes no headers; column headers come from the TablePattern (if the
            // control supports it). Without this the header row was always empty.
            var headers = new string?[cols];
            var table = el.Patterns.Table.PatternOrDefault;
            var headerEls = table?.ColumnHeaders.ValueOrDefault;
            if (headerEls != null)
            {
                for (int c = 0; c < cols && c < headerEls.Length; c++)
                    headers[c] = headerEls[c].Name;
            }

            var data = new string?[rows][];
            for (int r = 0; r < rows; r++)
            {
                data[r] = new string?[cols];
                for (int c = 0; c < cols; c++)
                {
                    var cell = grid.GetItem(r, c);
                    data[r][c] = cell.Name;
                }
            }
            return BuildTable(headers, data);
        }, ct);
    }

    /// <summary>
    /// The projection from raw UIA strings to the <see cref="TableData"/> DTO: every header and
    /// cell sanitised (A-13), a missing header "" rather than null. Separate from the pattern reads
    /// so it is unit-testable on plain strings — a grid cannot be faked headless.
    /// </summary>
    internal static TableData BuildTable(string?[] rawHeaders, string?[][] rawCells)
        => new(
            rawHeaders.Select(UiText.Sanitize).ToArray(),
            rawCells.Select(row => row.Select(UiText.Sanitize).ToArray()).ToArray());

    public Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs,
        FindKind kind = FindKind.Any, FindScope scope = FindScope.Foreground,
        string? windowTitle = null, bool includeOffscreen = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return PollAsync(async token =>
        {
            // Re-resolved every poll, so scope=window doubles as "wait for that app to open": a
            // window that does not exist yet is a failed poll, which PollAsync retries.
            var matches = await FindElementAsync(text, kind, scope, windowTitle, includeOffscreen, token).ConfigureAwait(false);
            return matches.Matches.Length > 0 ? matches.Matches[0] : null;
        }, timeoutMs, intervalMs, ct);
    }

    /// <summary>
    /// The retry loop behind <c>wait_for</c>, separated from UIA so it is unit-testable with a fake
    /// poll delegate. A poll that throws is recorded and retried — absorbing transient failure is
    /// the entire point of a wait, and before D-5 the first one ended it.
    /// </summary>
    /// <remarks>
    /// Polls at least once, so <c>timeout_ms: 0</c> means "check now" rather than "do nothing".
    /// The sleep is clamped to the remaining budget so the call cannot overshoot the deadline by up
    /// to a whole interval, and to a 10 ms floor so <c>interval_ms: 0</c> cannot peg the STA queue.
    /// On the deadline: null when at least one poll ran cleanly and found nothing (the documented
    /// contract), but <see cref="TimeoutException"/> when EVERY poll failed — answering "not found"
    /// when we never managed to look is the defect this method exists to fix.
    /// </remarks>
    internal static async Task<ElementInfo?> PollAsync(
        Func<CancellationToken, Task<ElementInfo?>> poll, int timeoutMs, int intervalMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        Exception? lastFailure = null;
        var anyCleanPoll = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hit = await poll(ct).ConfigureAwait(false);
                anyCleanPoll = true;
                if (hit is not null) return hit;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastFailure = ex; }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var delay = TimeSpan.FromMilliseconds(Math.Max(10, intervalMs));
            if (delay > remaining) delay = remaining;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        if (!anyCleanPoll && lastFailure is not null)
            throw new TimeoutException(
                $"wait_for: every poll failed within {timeoutMs} ms; last error: {lastFailure.Message}", lastFailure);

        return null;
    }

    /// <summary>
    /// B-6 (roadmap C4): the conditional wait. Gathers only the evidence the condition needs per
    /// poll, judges it with <see cref="WaitConditions.Evaluate"/>, and always answers with a
    /// <see cref="WaitForResult"/>.
    /// </summary>
    public Task<WaitForResult> WaitForAsync(WaitRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.TimeoutMs is < 0 or > 120_000)
            throw new ArgumentException($"timeoutMs must be between 0 and 120000, got {request.TimeoutMs}", nameof(request));
        if (request.IntervalMs is < 0 or > 5_000)
            throw new ArgumentException($"intervalMs must be between 0 and 5000, got {request.IntervalMs}", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException($"{WaitConditions.NameOf(request.Condition)} needs text: what to look for.", nameof(request));

        // Each condition gathers only the evidence it reads: active_window never walks a tree.
        Func<CancellationToken, Task<WaitEvidence>> gather = request.Condition switch
        {
            WaitCondition.ActiveWindow => async token =>
                new WaitEvidence(Windows: await _windows.ListAsync(true, false, token).ConfigureAwait(false)),
            WaitCondition.TextExists or WaitCondition.FocusedElement => async token =>
                new WaitEvidence(Snapshot: await SnapshotAsync(SnapshotRequestFor(request), token).ConfigureAwait(false)),
            _ => async token =>
                new WaitEvidence(Matches: (await FindElementAsync(request.Text!, request.Kind, request.Scope,
                    request.WindowTitle, request.IncludeOffscreen, token).ConfigureAwait(false)).Matches),
        };
        return WaitLoopAsync(request, gather, ct);
    }

    /// <summary>
    /// B-6: the poll loop behind <see cref="WaitForAsync(WaitRequest, CancellationToken)"/>,
    /// separated from UIA so it is unit-testable with a fake evidence gatherer — the same seam
    /// <see cref="PollAsync"/> is. Polls immediately, then every <c>IntervalMs</c> (10 ms floor,
    /// clamped to the remaining budget) until satisfied or the deadline; counts every poll in
    /// <c>Attempts</c>; a poll that throws is recorded and retried; when EVERY poll threw the
    /// detail is <c>"every poll failed: &lt;last message&gt;"</c> — a result, never a
    /// <see cref="TimeoutException"/> (C4 outranks D-5's throw, and the detail carries D-5's point).
    /// </summary>
    internal static async Task<WaitForResult> WaitLoopAsync(
        WaitRequest request, Func<CancellationToken, Task<WaitEvidence>> gather, CancellationToken ct)
    {
        var name = WaitConditions.NameOf(request.Condition);
        var text = request.Text ?? "";
        var clock = Stopwatch.StartNew();
        var deadline = TimeSpan.FromMilliseconds(request.TimeoutMs);
        int attempts = 0;
        bool anyCleanPoll = false;
        string lastDetail = "";
        Exception? lastFailure = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            try
            {
                var evidence = await gather(ct).ConfigureAwait(false);
                var (satisfied, detail, element) = WaitConditions.Evaluate(request.Condition, text, evidence);
                anyCleanPoll = true;
                lastDetail = detail;
                if (satisfied)
                    return new WaitForResult(true, name, clock.ElapsedMilliseconds, attempts, detail, element);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastFailure = ex; }   // D-5: a poll that throws is retried

            var remaining = deadline - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var delay = TimeSpan.FromMilliseconds(Math.Max(10, request.IntervalMs));
            if (delay > remaining) delay = remaining;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        // C4: a timeout is a result. When we never managed to look, the detail says so (D-5).
        var final = anyCleanPoll || lastFailure is null
            ? lastDetail
            : $"every poll failed: {lastFailure.Message}";
        return new WaitForResult(false, name, clock.ElapsedMilliseconds, attempts, final, null);
    }

    /// <summary>
    /// B-6: the snapshot one poll of a <c>text_exists</c> / <c>focused_element</c> wait takes —
    /// the find scope mapped onto the snapshot scope, the window title carried, A-5's DOM mode
    /// carried, no tree and the server's element budget.
    /// </summary>
    internal static SnapshotRequest SnapshotRequestFor(WaitRequest request)
    {
        var scope = request.Scope switch
        {
            FindScope.Window => SnapshotScope.Window,
            FindScope.Desktop => SnapshotScope.Desktop,
            _ => SnapshotScope.Foreground,
        };
        // No tree (the expensive half), the server's budget, the page when asked (A-5).
        return new SnapshotRequest(scope, scope == SnapshotScope.Window ? request.WindowTitle : null, false, 0, request.UseDom);
    }

    public Task FocusAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync<int>(() =>
        {
            var el = ResolveCached(elementId);
            el.Focus();
            return 0;
        }, ct);
    }

    private AutomationElement ResolveCached(string id)
    {
        lock (_cacheLock)
        {
            if (!_elementCache.TryGetValue(id, out var el))
                throw new KeyNotFoundException($"Element '{id}' not in cache");
            TouchCacheEntry(id);
            return el;
        }
    }

    private void TouchCacheEntry(string id)
    {
        if (!_cacheNodes.TryGetValue(id, out var node)) return;
        _cacheOrder.Remove(node);
        _cacheOrder.AddLast(node);
    }

    private void EvictOldestIfNeeded()
    {
        while (_elementCache.Count > MaxElementCacheEntries)
        {
            var oldest = _cacheOrder.First
                ?? throw new InvalidOperationException("Cache order desync");
            _cacheOrder.RemoveFirst();
            _cacheNodes.Remove(oldest.Value);
            _elementCache.Remove(oldest.Value);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Enqueue the COM teardown so it runs on the STA worker (UIA3Automation
        // holds STA-affine COM references — disposing from MTA can leak or
        // throw RPC_E_WRONG_THREAD on some Windows versions).
        try
        {
            _workQueue.Add(() =>
            {
                try { _automation.Dispose(); }
                catch (Exception) { /* best-effort during shutdown */ }
            });
        }
        catch (InvalidOperationException) { /* queue already completed */ }

        _workQueue.CompleteAdding();

        if (!_staThread.Join(TimeSpan.FromSeconds(2)))
        {
            // Worker hung; leak rather than block server shutdown.
            // (No safe way to abort an STA thread in .NET 9.)
        }

        _workQueue.Dispose();
    }
}
