using System.Runtime.InteropServices;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// D-10 R11 / D-11 R18: a top-level window owned by this process with three real Win32 child
/// controls — a DISABLED push button, a DISABLED check box, and an enabled push button — so UIA
/// can be pointed at controls whose state the test chose, without touching anything the user has
/// open. Same pattern as <see cref="OwnedWindow"/>: built-in classes (STATIC parent, BUTTON
/// children), its own message pump, shown without activation at the bottom of the z-order.
/// <para>
/// Every child's name starts with <see cref="Token"/>, as does the window title, so one find for
/// the token matches the window and all three children (deterministic multi-match), and
/// <c>Token + " disabled"</c> matches exactly the two disabled controls. Nothing here is ever
/// clicked or typed into: the tests only read, or ask UIA to act on a control that is disabled.
/// </para>
/// </summary>
internal sealed class OwnedControlsWindow : IDisposable
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_DISABLED = 0x08000000;
    private const uint BS_PUSHBUTTON = 0x0;
    private const uint BS_AUTOCHECKBOX = 0x3;
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
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>Unique per instance; the window title and every child name start with it.</summary>
    public string Token { get; } = "wmcp-ctl-" + Guid.NewGuid().ToString("N");
    public string Title => Token;
    public string DisabledButton => Token + " disabled button";
    public string DisabledCheckBox => Token + " disabled checkbox";
    public string EnabledButton => Token + " enabled button";

    public IntPtr Handle { get; private set; }

    public OwnedControlsWindow()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "wmcp-owned-controls" };
        thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(10));
    }

    public bool Exists => Handle != IntPtr.Zero && IsWindow(Handle);

    private void Pump()
    {
        Handle = CreateWindowExW(0, "STATIC", Title, WS_OVERLAPPEDWINDOW,
            140, 140, 520, 260, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Handle != IntPtr.Zero)
        {
            CreateWindowExW(0, "BUTTON", DisabledButton, WS_CHILD | WS_VISIBLE | WS_DISABLED | BS_PUSHBUTTON,
                10, 10, 460, 30, Handle, new IntPtr(101), IntPtr.Zero, IntPtr.Zero);
            CreateWindowExW(0, "BUTTON", DisabledCheckBox, WS_CHILD | WS_VISIBLE | WS_DISABLED | BS_AUTOCHECKBOX,
                10, 50, 460, 30, Handle, new IntPtr(102), IntPtr.Zero, IntPtr.Zero);
            CreateWindowExW(0, "BUTTON", EnabledButton, WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                10, 90, 460, 30, Handle, new IntPtr(103), IntPtr.Zero, IntPtr.Zero);
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            // Bottom of the z-order, never activated (OwnedWindow's reasoning): never the
            // foreground window another test walks.
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            // WM_CLOSE destroys a STATIC window but posts no WM_QUIT, so without this the next
            // GetMessageW blocks for ever and every fixture leaves a thread behind.
            if (Handle != IntPtr.Zero && !IsWindow(Handle)) break;
        }
    }

    /// <summary>Closes the window (and its children) and waits until it is really gone.</summary>
    public void Close()
    {
        if (!Exists) return;
        PostMessageW(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 40 && Exists; i++) Thread.Sleep(50);
        if (Exists) DestroyWindow(Handle);   // last resort; fails cross-thread, but harmless
    }

    public void Dispose()
    {
        Close();
        _ready.Dispose();
    }
}
