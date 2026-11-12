using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IUIAutomationService : IDisposable
{
    Task<ElementTree> GetStateAsync(CancellationToken ct = default);
    /// <param name="windowTitle">
    /// Required with <see cref="FindScope.Window"/> and rejected with any other scope: the title of
    /// the top-level window to search, matched exact-then-substring, case-insensitively.
    /// </param>
    /// <param name="includeOffscreen">
    /// When false (default) elements reporting <c>IsOffscreen</c> or with empty bounds are dropped
    /// before the result cap, so an on-screen match cannot be crowded out (checklist D-7). An
    /// <c>Edit</c> with real bounds is kept either way — browsers over-report it as off-screen.
    /// </param>
    Task<FindElementResult> FindElementAsync(string text, FindKind kind = FindKind.Any,
        FindScope scope = FindScope.Foreground, string? windowTitle = null,
        bool includeOffscreen = false, CancellationToken ct = default);
    /// <summary>
    /// E-4: the same find with the filters that tell controls apart (automation id, control type,
    /// class name), a subtree to search instead of a window (<c>Within</c>) and a page of the
    /// matches. The positional overload above is this one with every new field at its default.
    /// </summary>
    Task<FindElementResult> FindElementAsync(FindElementRequest request, CancellationToken ct = default);
    Task<ElementInfo> GetElementAsync(string elementId, CancellationToken ct = default);
    Task<string> GetTextAsync(string elementId, CancellationToken ct = default);
    Task<AssertResult> AssertElementAsync(string elementId, string state, string? expected = null, CancellationToken ct = default);
    Task<InteractResult> InteractAsync(string elementId, string action, string? value, CancellationToken ct = default);
    Task<TableData> GetTableAsync(string elementId, CancellationToken ct = default);
    /// <summary>
    /// Polls <see cref="FindElementAsync"/> until a match appears. A poll that throws is retried,
    /// never fatal (checklist D-5). Returns null when polls ran cleanly and found nothing; throws
    /// <see cref="TimeoutException"/> when <b>every</b> poll failed — "never managed to look" is not
    /// the same answer as "looked and did not find it".
    /// </summary>
    Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs,
        FindKind kind = FindKind.Any, FindScope scope = FindScope.Foreground,
        string? windowTitle = null, bool includeOffscreen = false, CancellationToken ct = default);

    /// <summary>
    /// B-6 (roadmap C4): wait for one of the five <see cref="WaitCondition"/>s and ALWAYS answer
    /// with a <see cref="WaitForResult"/> — a timeout is <c>Satisfied:false</c> carrying the last
    /// poll's detail, never a <see cref="TimeoutException"/> and never null. A poll that throws is
    /// retried (D-5); when every poll threw, the detail says so.
    /// </summary>
    Task<WaitForResult> WaitForAsync(WaitRequest request, CancellationToken ct = default);
    Task FocusAsync(string elementId, CancellationToken ct = default);

    /// <summary>
    /// A-2: the whole labelled desktop in one call — window list (A-1), cursor (A-11), every
    /// interactive element with its centre and action hint, the scrollable regions (A-3), and the
    /// budget's verdict (A-4). Element ids are valid until the next snapshot (roadmap C5).
    /// </summary>
    Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default);
}
