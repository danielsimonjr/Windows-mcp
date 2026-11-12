using System.Runtime.InteropServices;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// A real top-level window owned by this process: created on its own message-pumping thread
/// (cross-thread <c>GetWindowText</c> and <c>PostMessage</c> both need one) from the built-in
/// STATIC class, so no window class has to be registered and DefWindowProc handles WM_CLOSE.
/// <para>
/// Shared by <c>WindowServiceExecuteTests</c> (B-10) and <c>WindowServiceBoundsTests</c> (B-9):
/// both need a real window this process owns, so a service can act on it for real without
/// touching anything the user has open. Moved out of the first of those classes verbatim when
/// B-9 needed a second caller; the behaviour is unchanged.
/// </para>
/// </summary>
internal sealed class OwnedWindow : IDisposable
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_CLOSE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly ManualResetEventSlim _ready = new(false);

    public IntPtr Handle { get; private set; }
    public string Title { get; }

    public OwnedWindow(string title)
    {
        Title = title;
        var thread = new Thread(Pump) { IsBackground = true, Name = "wmcp-owned-window" };
        thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException($"{nameof(OwnedWindow)} '{title}': the window thread did not create its window within 10 s.");
    }

    public bool Exists => Handle != IntPtr.Zero && IsWindow(Handle);

    private void Pump()
    {
        Handle = CreateWindowExW(0, "STATIC", Title, WS_OVERLAPPEDWINDOW,
            120, 120, 400, 300, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Handle != IntPtr.Zero)
        {
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            // Bottom of the z-order, never activated: parallel test classes that walk "the
            // first window on the desktop" (UIAutomationDomSnapshotTests) must not pick a
            // window that is about to be destroyed.
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    public void Dispose()
    {
        if (Exists)
        {
            // DestroyWindow only works from the owning thread; WM_CLOSE reaches it through
            // the pump and DefWindowProc destroys the window for us.
            PostMessageW(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < 20 && Exists; i++) Thread.Sleep(50);
            if (Exists) DestroyWindow(Handle);   // last resort; fails cross-thread, but harmless
        }
        _ready.Dispose();
    }
}
