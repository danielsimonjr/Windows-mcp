using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class UIAutomationTools
{
    private readonly IUIAutomationService _uia;

    public UIAutomationTools(IUIAutomationService uia)
    {
        _uia = uia;
    }

    private static FindKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "any" => FindKind.Any,
        "interactive" => FindKind.Interactive,
        "text" => FindKind.Text,
        "scrollable" => FindKind.Scrollable,
        _ => throw new ArgumentException($"Unknown kind '{kind}'; expected any|interactive|text|scrollable")
    };

    private static FindScope ParseScope(string scope) => scope.ToLowerInvariant() switch
    {
        "foreground" => FindScope.Foreground,
        "window" => FindScope.Window,
        "desktop" => FindScope.Desktop,
        _ => throw new ArgumentException($"Unknown scope '{scope}'; expected foreground|window|desktop")
    };

    /// <summary>
    /// A window title is meaningful only with scope=window. Rejecting the mismatch rather than
    /// ignoring the argument follows D-4's `expected` rule — silently ignoring an argument is how
    /// the D-2 `select` bug went unnoticed.
    /// </summary>
    private static (FindScope Scope, string? Window) ParseTarget(string scope, string? window)
    {
        var parsed = ParseScope(scope);
        if (parsed == FindScope.Window && string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("scope=window requires window: the title of the window to search.", nameof(window));
        if (parsed != FindScope.Window && !string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("window is only used with scope=window.", nameof(window));
        return (parsed, string.IsNullOrWhiteSpace(window) ? null : window);
    }

    private static SnapshotScope ParseSnapshotScope(string scope) => scope.ToLowerInvariant() switch
    {
        "desktop" => SnapshotScope.Desktop,
        "foreground" => SnapshotScope.Foreground,
        "window" => SnapshotScope.Window,
        _ => throw new ArgumentException($"Unknown scope '{scope}'; expected desktop|foreground|window")
    };

    [McpServerTool(Title = "Desktop snapshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("One call for the whole desktop (parity A-2): every open window, the foreground one, the cursor, and every interactive element with its centre coordinates and an action hint (click/fill/toggle/select/slide/scroll), plus scrollable regions with their scroll percentages. Default format is compact text; format:'json' returns the same as JSON (with the element tree when include_tree is set). Element ids (el_N) are valid until the next snapshot (screenshot with annotate, wait_for's text/focused conditions and scrape with source dom take one too) and work with click (use the centre coordinates), interact_element and get_element. scope: desktop (default, every non-minimised window, topmost first) | foreground | window (with 'window' = a title, exact then substring). max_elements caps the walk (0 = the server default, --max-tree-elements, 500); when the cap is hit the result says it was truncated (the text form adds how to narrow the view; json carries Truncated and ElementLimit). use_dom (browser DOM mode, Chromium: chrome/msedge/brave/opera/vivaldi): for every browser window in scope walk only the web page — the RootWebArea document — instead of the whole window, so the address bar and tab strip are left out, and add a Pages section per browser window: the page document's id, title, URL, vertical scroll percent and the visible page text in document order (below-the-fold text appears after scrolling). The page document itself is scrollable, never interactive. A browser window with no page document (still loading, or Firefox, which is not supported yet) is walked whole and its Pages entry says so.")]
    public async Task<string> Snapshot(
        [Description("desktop | foreground | window")] string scope = "desktop",
        [Description("Window title, exact or substring, case-insensitive; only with scope=window")] string? window = null,
        [Description("Also return the element tree (json only)")] bool include_tree = false,
        [Description("Element budget for this call; 0 = the server default (--max-tree-elements)")] int max_elements = 0,
        [Description("text (default, compact) | json")] string format = "text",
        [Description("Walk browser windows from the web page (the RootWebArea document) instead of the whole window and add a Pages section with each page's title, URL, scroll percent and visible text (default: false; Chromium browsers only)")] bool use_dom = false)
    {
        var parsed = ParseSnapshotScope(scope);
        if (parsed == SnapshotScope.Window && string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("scope=window requires window: the title of the window to snapshot.", nameof(window));
        if (parsed != SnapshotScope.Window && !string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("window is only used with scope=window.", nameof(window));
        if (max_elements < 0)
            throw new ArgumentException($"max_elements must be 0 (the server default) or positive, got {max_elements}");
        bool json = format.ToLowerInvariant() switch
        {
            "text" => false,
            "json" => true,
            _ => throw new ArgumentException($"Unknown format '{format}'; expected text|json"),
        };
        var request = new SnapshotRequest(parsed, string.IsNullOrWhiteSpace(window) ? null : window, include_tree, max_elements, use_dom);
        var result = await _uia.SnapshotAsync(request);
        return json ? JsonSerializer.Serialize(result) : SnapshotRenderer.Render(result);
    }

    [McpServerTool(Title = "Get UI state", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Return the UI element tree of the foreground application (three levels deep, bounded by --max-tree-elements; the root reports Truncated/ElementLimit when the budget stopped the walk). For the whole desktop with centre coordinates and action hints, use snapshot.")]
    public async Task<string> GetState()
    {
        var tree = await _uia.GetStateAsync();
        return JsonSerializer.Serialize(tree);
    }

    [McpServerTool(Title = "Find element", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Find UI elements whose name contains text (empty text = every element the filters allow). " +
        "Searches the FOREGROUND window by default, resolved at call time — for a multi-step " +
        "workflow pass scope:\"window\" with window:<title> so a notification or another app " +
        "stealing focus cannot change what is searched; scope:\"desktop\" searches every top-level " +
        "window. Returns at most 20 matches, capped AFTER filtering. A UI element that disappears " +
        "mid-search is skipped, never fatal.")]
    public async Task<string> FindElement(
        [Description("Text to search for in element names (substring, case-insensitive); empty matches all")] string text,
        [Description("Element kind: any | interactive (Button, ListItem, MenuItem, Edit, CheckBox, RadioButton, ComboBox, Hyperlink, SplitButton, TabItem, TreeItem, DataItem, HeaderItem, Spinner, Slider, ScrollBar, Document) | text (Text, Edit, Document) | scrollable")] string kind = "any",
        [Description("Search scope: foreground (default) | window (needs 'window') | desktop")] string scope = "foreground",
        [Description("Window title to search, exact or substring, case-insensitive; only with scope=window")] string? window = null,
        [Description("Include off-screen elements (collapsed panes, virtualised rows, minimised windows). Default false; an Edit with real bounds is kept either way")] bool include_offscreen = false)
    {
        var (parsedScope, windowTitle) = ParseTarget(scope, window);
        var result = await _uia.FindElementAsync(text, ParseKind(kind), parsedScope, windowTitle, include_offscreen);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Title = "Get element", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Get detailed information about a UI element by its ID; the ElementId in the result is the id passed in.")]
    public async Task<string> GetElement(
        [Description("Element ID returned by snapshot, find_element or get_state")] string element_id)
    {
        var info = await _uia.GetElementAsync(element_id);
        return JsonSerializer.Serialize(info);
    }

    [McpServerTool(Title = "Get text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Extract text content from a UI element (faster than OCR).")]
    public async Task<string> GetText(
        [Description("Element ID to read text from")] string element_id)
    {
        return await _uia.GetTextAsync(element_id);
    }

    [McpServerTool(Title = "Assert element", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Assert a UI element state: exists, enabled, checked, visible, focused, or value (needs expected: an exact match against the element's ValuePattern value, else its Name). Returns 'PASS' or 'FAIL: <state> — observed <what was found>', e.g. 'focus is on Button 'Save'', 'value is 'x' (from ValuePattern)', 'toggle state Off', 'element no longer available' (its window closed since the id was issued). An unknown state, 'value' without expected, or expected with another state is an error.")]
    public async Task<string> AssertElement(
        [Description("Element ID to check")] string element_id,
        [Description("State to assert: exists, enabled, checked, value, visible, focused")] string state,
        [Description("Expected value; only with state=value")] string? expected = null)
    {
        var result = await _uia.AssertElementAsync(element_id, state, expected);
        return result.Pass ? "PASS" : $"FAIL: {result.State} — observed {result.Observed}";
    }

    [McpServerTool(Title = "Interact with element", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description("Act on a UI element by id. action: click (InvokePattern, else SelectionItem, else Toggle, else a physical click at the element's centre), invoke, toggle, select (no value: select this item; value: pick the child item with that name in a combo/list), focus, type (value = text; a writable ValuePattern replaces the whole value, otherwise it is typed at the caret), expand / collapse (ExpandCollapsePattern: tree nodes, combo boxes, menus, collapsible panes; Detail is the state afterwards, or 'already Expanded'), set_value (a control with RangeValuePattern takes value = a number, '.' as the decimal point, and one outside the range is refused naming the minimum and maximum, which are the provider's numbers: a Win32 trackbar reports 0-100 percent of its own range; any other control gets value as text through a writable ValuePattern; Detail is the value the control shows afterwards, and a text write the control accepted but ignored, so that its value did not change, is refused; no focus call and no typing fallback, though a provider may bring the control's window forward), scroll_into_view (ScrollItemPattern, no pointer movement; where the control does not offer the pattern, an element already on screen succeeds with nothing done and any other gets an error that names the nearest scrollable container's id to pass to scroll). Returns {ElementId, Action, Method, Detail} saying which pattern or fallback fired and what the control shows afterwards; an unsupported pattern errors naming the pattern and the control, and a control that reports disabled is refused before anything is touched (scroll_into_view excepted).")]
    public async Task<string> InteractElement(
        [Description("Element ID from snapshot, find_element or get_state")] string element_id,
        [Description("click | invoke | toggle | select | focus | type | expand | collapse | set_value | scroll_into_view")] string action,
        [Description("Text for 'type'; child item name for 'select' (optional); for 'set_value' a number on a range control, otherwise the text to set. Must be empty for expand, collapse and scroll_into_view; ignored by the rest")] string? value = null)
    {
        var result = await _uia.InteractAsync(element_id, action, value);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Title = "Get table", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Extract tabular data from a UI element via GridPattern.")]
    public async Task<string> GetTable(
        [Description("Element ID of a grid/table control")] string element_id)
    {
        var table = await _uia.GetTableAsync(element_id);
        return JsonSerializer.Serialize(table);
    }

    [McpServerTool(Title = "Wait for condition", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Wait until something is true on screen, polling until it is or the timeout passes. condition: " +
        "element_exists (default; an element whose name contains text — the same kind/scope/window/include_offscreen " +
        "filters as find_element, and scope:\"window\" is re-resolved on every poll so it also serves as 'wait for that app " +
        "to open') | element_enabled (that element, and enabled) | focused_element (the element with keyboard focus has " +
        "that name) | text_exists (the text is anywhere in a snapshot of the scope: element names and values, scrollable " +
        "regions, and with use_dom:true the browser page's words) | active_window (the foreground window's title matches " +
        "text: exact, substring or fuzzy 70+). Aliases: element, enabled, focused, text, window. Always returns " +
        "{Satisfied, Condition, ElapsedMs, Attempts, Detail, Element?}: a timeout is Satisfied:false with the last Detail, " +
        "not an error; a poll that fails is retried, and when every poll failed Detail says so. A text_exists or " +
        "focused_element poll is a snapshot, so it replaces the previous snapshot's element ids.")]
    public async Task<string> WaitFor(
        [Description("What to look for: an element name for the element conditions, a window title for active_window, on-screen text for text_exists (substring, case-insensitive)")] string text,
        [Description("Timeout in milliseconds")] int timeout_ms = 10000,
        [Description("Poll interval in milliseconds")] int interval_ms = 500,
        [Description("Element kind: any | interactive | text | scrollable")] string kind = "any",
        [Description("Search scope: foreground (default) | window (needs 'window') | desktop")] string scope = "foreground",
        [Description("Window title to search; only with scope=window")] string? window = null,
        [Description("Include off-screen elements. Default false — otherwise the wait can succeed on an element that is not visible yet")] bool include_offscreen = false,
        [Description("What to wait for: element_exists (default) | element_enabled | focused_element | text_exists | active_window (aliases: element, enabled, focused, text, window)")] string condition = "element_exists",
        [Description("For text_exists/focused_element in a browser: read the web page (the RootWebArea document, A-5) instead of the window chrome")] bool use_dom = false)
    {
        if (timeout_ms is < 0 or > 120000)
            throw new ArgumentException($"timeout_ms must be between 0 and 120000, got {timeout_ms}", nameof(timeout_ms));
        if (interval_ms is < 0 or > 5000)
            throw new ArgumentException($"interval_ms must be between 0 and 5000, got {interval_ms}", nameof(interval_ms));
        var parsedCondition = condition.ToLowerInvariant() switch
        {
            "element_exists" or "element" => WaitCondition.ElementExists,
            "element_enabled" or "enabled" => WaitCondition.ElementEnabled,
            "focused_element" or "focused" => WaitCondition.FocusedElement,
            "text_exists" or "text" => WaitCondition.TextExists,
            "active_window" or "window" => WaitCondition.ActiveWindow,
            _ => throw new ArgumentException(
                $"Unknown condition '{condition}'; wait_for returns when one of these appears or holds: element_exists|element_enabled|focused_element|text_exists|active_window (aliases element|enabled|focused|text|window)", nameof(condition)),
        };
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException($"{WaitConditions.NameOf(parsedCondition)} needs text: what to look for.", nameof(text));

        var (parsedScope, windowTitle) = ParseTarget(scope, window);
        var request = new WaitRequest(parsedCondition, text, timeout_ms, interval_ms, ParseKind(kind), parsedScope, windowTitle, include_offscreen, use_dom);
        return JsonSerializer.Serialize(await _uia.WaitForAsync(request));
    }
}
