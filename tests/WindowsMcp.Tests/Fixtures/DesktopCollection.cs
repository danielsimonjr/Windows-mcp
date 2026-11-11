namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// There is one mouse pointer, one desktop, and — on Windows 11 — one Notepad process. Every test
/// class that MOVES the pointer, PAINTS on the screen, asserts on live screen PIXELS, or opens a
/// NOTEPAD WINDOW through <see cref="NotepadFixture"/> belongs to this collection, so xunit never
/// runs two of them at once.
/// <para>
/// POINTER AND PIXELS — the original reason. The whole desktop bracket
/// (<c>--filter "Category=UIAutomation"</c>) failed two tests that pass in isolation —
/// <c>ScreenshotCursorTests.CaptureAsync_with_include_cursor_draws_the_cursor_and_reports_how</c>
/// and <c>ScreenshotWgcCaptureTests.CaptureAsync_wgc_leaves_the_pointer_out_of_the_frame</c>.
/// xunit runs test CLASSES in parallel, and both of those classes park the real pointer with
/// <c>InputService.HoverAsync</c> and then compare the pixels under it: one class moves the cursor
/// into (or out of) the other's captured rect between its "before" and "after" captures, so the
/// byte comparison the assertion rests on stops being about what the test itself changed. The
/// failure only ever appears when the classes run together, never alone.
/// </para>
/// <para>
/// NOTEPAD WINDOWS — why the collection grew. The same bracket later failed twelve tests across
/// <c>UIAutomationServiceTests</c>, <c>UIAutomationSnapshotDesktopTests</c>,
/// <c>WindowForegroundDesktopTests</c>, <c>NotepadFixtureSelfTests</c> and one pointer test in
/// <c>ScreenshotWgcCaptureTests</c>, each class again passing alone. Four <see cref="NotepadFixture"/>
/// instances had launched Notepad within the same second (four windowless notepad.exe launcher
/// processes with identical start times). The modern Notepad is ONE PROCESS hosting every window,
/// so there is no per-process isolation to be had and the fixture has to identify its window by
/// DIFFING the Notepad-owned window inventory across its own launch
/// (<see cref="NotepadFixture.SelectOpenedWindow"/>). Two fixtures launching concurrently each see
/// the other's new window in that diff, pick the wrong one, and their classes then minimise, close
/// and type into each other's windows. Serialising the classes is the only fix: two fixtures must
/// never launch Notepad at the same time.
/// </para>
/// <para>
/// Membership rule — join this collection when a class does any of: moves the pointer
/// (<c>HoverAsync</c> / <c>ClickAsync</c> / <c>SetCursorPos</c>), draws on the desktop, makes an
/// assertion whose outcome depends on the actual pixels on screen (a before/after byte comparison,
/// a colour sample, a GDI-vs-WGC agreement check), or constructs a <see cref="NotepadFixture"/> —
/// as an <c>IClassFixture&lt;NotepadFixture&gt;</c> or with <c>new</c> inside a test, both open a
/// window. The last two rules are the same rule seen from two sides: a Notepad window appearing,
/// moving or closing also rewrites the pixels a capture-comparing class is in the middle of
/// comparing.
/// </para>
/// <para>
/// FOREGROUND — D-10 R24c. Also join when a class READS the foreground and asserts on what it
/// finds there (<c>get_state</c>, a foreground <c>find_element</c> or <c>snapshot</c>), or may
/// CHANGE it (focus, an <c>interact_element</c> verb on a control of its own window): the one
/// moves the ground under the other. <c>UIAutomationServiceElementCacheIntegrationTests</c> failed
/// intermittently beside the D-10 refusal tests for exactly this reason.
/// </para>
/// <para>
/// Needing an interactive desktop is NOT enough on its own: <c>ScreenshotServiceTests</c>,
/// <c>OcrServiceLiveTests</c> and <c>HttpTransportScreenshotImageTests</c> capture the screen but
/// assert only on size, format, metadata and PNG/JPEG magic bytes; <see cref="EdgeCollection"/>'s
/// classes inject nothing, read no pixels and open no Notepad; and
/// <c>NotepadFixtureHelperTests</c> exercises only the fixture's pure static helpers, launching
/// nothing. They stay parallel, which is what keeps the bracket's wall-clock time down.
/// </para>
/// <para>
/// There is deliberately no fixture here and no <c>DisableParallelization</c>: a collection is
/// xunit's unit of parallelism, so membership alone serialises the classes in it. In particular
/// this is NOT an <c>ICollectionFixture&lt;NotepadFixture&gt;</c> — every class keeps its own
/// <see cref="NotepadFixture"/> instance and its own window, and only the SCHEDULING changes. The
/// definition stays empty on purpose.
/// </para>
/// </summary>
[CollectionDefinition(DesktopCollection.Name)]
public sealed class DesktopCollection
{
    /// <summary>The name every pointer / pixel / Notepad test class carries on <c>[Collection]</c>.</summary>
    public const string Name = "Interactive desktop";
}
