using System.Text.Json.Serialization;

namespace WindowsMcp.Abstractions.Models;

/// <param name="Scroll">
/// A-3: scroll position and scrollability when the element exposes a ScrollPattern, null when it
/// does not. Additive — every construction that predates A-3 keeps compiling.
/// </param>
public record ElementInfo(
    string ElementId,
    string Name,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    Bounds? Bounds,
    string? Value,
    bool? IsChecked,
    bool? IsSelected,
    ScrollInfo? Scroll = null);

public record Bounds(int X, int Y, int Width, int Height);

/// <summary>
/// A-3: what a ScrollPattern reports. Percentages are 0-100; a non-scrollable axis still reports
/// its percent (UIA gives 0 or -1 there — the traverser normalises), so the flags are what say
/// whether scrolling that axis means anything.
/// </summary>
public record ScrollInfo(
    double VerticalPercent,
    double HorizontalPercent,
    bool VerticallyScrollable,
    bool HorizontallyScrollable);

/// <param name="Truncated">
/// A-4: the walk that produced this tree hit the element budget and stopped early. Set on the
/// ROOT only; children keep the default. Omitted from JSON when false so an untruncated tree
/// serialises exactly as it did before A-4.
/// </param>
/// <param name="ElementLimit">A-4: the budget that was in force. Omitted from JSON when 0.</param>
public record ElementTree(
    ElementInfo Root,
    ElementTree[] Children,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ElementLimit = 0);

public record FindElementResult(ElementInfo[] Matches);

public enum FindKind { Interactive, Text, Scrollable, Any }

/// <summary>
/// Which part of the UI tree <c>find_element</c> / <c>wait_for</c> walk (checklist D-5).
/// <see cref="Foreground"/> is the default: the window the agent is acting on, resolved at call
/// time. <see cref="Window"/> pins the search to one window by title so a multi-step workflow is
/// unaffected by focus moving. <see cref="Desktop"/> walks every top-level window — what the tool
/// did implicitly before D-5, and the reason one stale element could fail the whole call.
/// </summary>
public enum FindScope { Foreground, Window, Desktop }

public record TableData(string[] Headers, string[][] Rows);

/// <summary>
/// What <c>interact_element</c> actually did. <paramref name="Method"/> is the UIA pattern that
/// fired (InvokePattern, SelectionItemPattern, TogglePattern, ValuePattern, Focus) or the fallback
/// (PhysicalClick, Keyboard); <paramref name="Detail"/> adds the click point, toggle state, or item name.
/// </summary>
public record InteractResult(string ElementId, string Action, string Method, string? Detail);

/// <summary>
/// Outcome of <c>assert_element</c>. <paramref name="Observed"/> is what the element actually
/// showed — on FAIL the reason (<c>disabled</c>, <c>toggle state Off</c>, <c>focus is on Button
/// 'Save'</c>, <c>value is 'x' (from ValuePattern)</c>, <c>element no longer available</c>), on
/// PASS the matching observation.
/// </summary>
public record AssertResult(string ElementId, string State, bool Pass, string Observed);

// ---- A-2 snapshot -------------------------------------------------------------------------

/// <summary>How much of the desktop a <c>snapshot</c> walks (A-2).</summary>
public enum SnapshotScope { Desktop, Foreground, Window }

/// <param name="MaxElements">0 = use the server default (<see cref="UiTreeOptions"/>).</param>
/// <param name="UseDom">
/// A-5 phase 1: walk only the web page (the <c>RootWebArea</c> document) under every browser
/// window among the targets, instead of the whole window, and report a <see cref="SnapshotPage"/>
/// for each. Off by default, so a snapshot that does not ask for it is unchanged.
/// </param>

public record SnapshotRequest(
    SnapshotScope Scope = SnapshotScope.Desktop,
    string? WindowTitle = null,
    bool IncludeTree = false,
    int MaxElements = 0,
    bool UseDom = false);

/// <summary>
/// A-4 (roadmap C7): the process-level element budget, set from <c>WINDOWSMCP_MAX_TREE_ELEMENTS</c>
/// by <c>ServerOptions</c> and injected into the service — never read from the environment inside
/// a service. A per-call <c>max_elements</c> overrides it.
/// </summary>
/// <param name="Profile">
/// A-14: report per-stage timings on the snapshot (<c>--profile-snapshot</c>). Off by default, so
/// an unprofiled response is byte-identical to a pre-A-14 one.
/// </param>
/// <param name="MaxCachedElements">
/// D-11: the cap on element ids held outside the current snapshot (find_element, get_state); the
/// least recently used goes first. The service raises it to twice <paramref name="MaxElements"/>
/// when that is larger, so one call's own ids cannot evict each other.
/// </param>
public record UiTreeOptions(int MaxElements, bool Profile = false, int MaxCachedElements = 5000)
{
    public static UiTreeOptions Default { get; } = new(500);
}

/// <summary>
/// A-2: one interactive element in a snapshot. <paramref name="CenterX"/>/<paramref name="CenterY"/>
/// are virtual-desktop pixels (roadmap C1) — what <c>click</c>/<c>drag</c>/<c>scroll</c> accept.
/// <paramref name="ElementId"/> is valid until the next snapshot (roadmap C5).
/// </summary>
public record SnapshotElement(
    string ElementId,
    string Window,
    string ControlType,
    string Name,
    int CenterX,
    int CenterY,
    Bounds Bounds,
    string Action,
    bool Focused,
    bool IsPassword,
    string? Value,
    string? Toggle,
    string? Expand,
    string? Shortcut,
    double? RangeValue,
    double? RangeMin,
    double? RangeMax);

/// <summary>A-3: one scrollable region in a snapshot.</summary>
public record SnapshotScrollable(
    string ElementId,
    string Window,
    string ControlType,
    string Name,
    int CenterX,
    int CenterY,
    Bounds Bounds,
    ScrollInfo Scroll);

/// <summary>
/// A-5 phase 1: one browser window's web page, as walked from its <c>RootWebArea</c> document.
/// </summary>
/// <param name="Window">Title of the browser window the page was found under.</param>
/// <param name="DocumentId">
/// The <c>el_N</c> id issued to the page document itself — the same id the scrollable list carries,
/// and what <c>get_element</c> / <c>scroll</c> accept. Null when no page document was found.
/// </param>
/// <param name="Title">The document's Name — the page's &lt;title&gt;. Null when there is no page.</param>
/// <param name="Url">The document's ValuePattern value — the page URL. Null when there is no page.</param>
/// <param name="Scroll">The document's scroll position, null when it exposes no scroll pattern.</param>
/// <param name="Text">
/// The visible page text: the Names of the Text nodes the walk admitted under the document, in
/// document order. Empty when there is no page (or the page has no visible text).
/// </param>
/// <param name="Note">
/// Why this page is empty — set only when the window is a browser but no page document was found
/// (still loading, Firefox, a non-web page), in which case the window was walked whole. Null on success.
/// </param>

public record SnapshotPage(
    string Window,
    string? DocumentId,
    string? Title,
    string? Url,
    ScrollInfo? Scroll,
    string[] Text,
    string? Note);

/// <param name="ElementCount">Every element the walk visited, before the interactive/scrollable split.</param>
/// <param name="Pages">
/// A-5 phase 1: one entry per browser window among the walked targets. Null when the request did
/// not set <see cref="SnapshotRequest.UseDom"/> — so a non-DOM response is byte-identical to a
/// pre-A-5 one — and an array (possibly empty) when it did.
/// </param>

public record SnapshotResult(
    WindowInfo[] Windows,
    WindowInfo? ActiveWindow,
    CursorPosition Cursor,
    int CursorMonitorIndex,
    SnapshotElement[] Interactive,
    SnapshotScrollable[] Scrollable,
    ElementTree? Tree,
    bool Truncated,
    int ElementLimit,
    int ElementCount,
    long CaptureMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StageTiming[]? Stages = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SnapshotPage[]? Pages = null);

// ---- B-6 wait_for conditions --------------------------------------------------------------

/// <summary>
/// B-6: what <c>wait_for</c> is waiting for. The canonical names on the wire are the snake_case
/// forms (<c>element_exists</c>, <c>element_enabled</c>, <c>focused_element</c>,
/// <c>text_exists</c>, <c>active_window</c>); the tool also accepts upstream's short aliases
/// (<c>element|enabled|focused|text|window</c>).
/// </summary>
public enum WaitCondition { ElementExists, ElementEnabled, FocusedElement, TextExists, ActiveWindow }

/// <summary>
/// B-6: one wait. <paramref name="Text"/> is required (non-blank) by every condition — it is the
/// element name for the element conditions, the window title for <see cref="WaitCondition.ActiveWindow"/>
/// and the on-screen text for <see cref="WaitCondition.TextExists"/>.
/// </summary>
/// <param name="TimeoutMs">0..120000; 0 means "check once, now".</param>
/// <param name="IntervalMs">0..5000; clamped to a 10 ms floor and to the remaining budget.</param>
/// <param name="UseDom">
/// A-5's browser DOM mode, only meaningful for <see cref="WaitCondition.TextExists"/> and
/// <see cref="WaitCondition.FocusedElement"/> (the two that read a snapshot); accepted and ignored for the others.
/// </param>
public record WaitRequest(
    WaitCondition Condition,
    string? Text,
    int TimeoutMs = 10000,
    int IntervalMs = 500,
    FindKind Kind = FindKind.Any,
    FindScope Scope = FindScope.Foreground,
    string? WindowTitle = null,
    bool IncludeOffscreen = false,
    bool UseDom = false);

/// <summary>
/// B-6 (roadmap C4): the answer a wait always gives. A timeout is
/// <see cref="Satisfied"/> false with the last <see cref="Detail"/>, never an exception and never
/// the string "null".
/// </summary>
/// <param name="Condition">The canonical snake_case condition name.</param>
/// <param name="Element">The element that satisfied the wait; omitted from JSON when there is none.</param>
public record WaitForResult(
    bool Satisfied,
    string Condition,
    long ElapsedMs,
    int Attempts,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ElementInfo? Element = null);
