using System.Runtime.InteropServices;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// E-4: a top-level window owned by this process whose real Win32 child controls give
/// <c>find_element</c>'s new filters something exact to find, through real UIA:
/// <list type="bullet">
/// <item>an UNNAMED push button (<see cref="UnnamedButtonId"/>) — the checklist's "done when": an
/// unnamed button addressed directly by automation id inside a named window;</item>
/// <item>two push buttons with the SAME name (<see cref="Twin"/>), told apart only by their
/// automation ids <see cref="TwinAId"/> and <see cref="TwinBId"/>;</item>
/// <item>an EDIT (<see cref="EditId"/>, UIA class name <c>Edit</c>);</item>
/// <item>a group box (<see cref="GroupId"/>) that is the PARENT window of a button named
/// <see cref="Inner"/> (<see cref="InnerInGroupId"/>), and outside it a second button with the same
/// name (<see cref="InnerOutsideId"/>) — so a <c>within</c> search on the group can be told apart
/// from a whole-window search;</item>
/// <item>a LISTBOX (<see cref="ListId"/>) of <see cref="RowCount"/> rows named
/// <see cref="Row"/>, for paging.</item>
/// </list>
/// UIA's Win32 proxy reports a child window's control id (the <c>hMenu</c> argument of
/// <c>CreateWindowExW</c>) as its AutomationId, as a decimal string. Same shape as
/// <see cref="OwnedControlsWindow"/> and <see cref="OwnedPatternControlsWindow"/> (a separate class
/// so their exact-match counts are untouched): built-in classes, its own message pump, shown
/// without activation at the bottom of the z-order. Nothing here is clicked or typed into: the
/// tests only read.
/// </summary>
internal sealed class OwnedFindFiltersWindow : IDisposable
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint BS_PUSHBUTTON = 0x0;
    private const uint BS_GROUPBOX = 0x7;
    private const uint ES_AUTOHSCROLL = 0x0080;
    private const uint LBS_NOTIFY = 0x0001;
    private const uint LBS_NOINTEGRALHEIGHT = 0x0100;
    private const uint LB_ADDSTRING = 0x0180;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_CLOSE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    public const int RowCount = 40;

    public const string UnnamedButtonId = "301";
    public const string TwinAId = "302";
    public const string TwinBId = "303";
    public const string EditId = "304";
    public const string GroupId = "305";
    public const string InnerInGroupId = "306";
    public const string InnerOutsideId = "307";
    public const string ListId = "308";

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

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
    public string Token { get; } = "wmcp-e4-" + Guid.NewGuid().ToString("N");
    public string Title => Token;
    public string Twin => Token + " twin";
    public string Group => Token + " group";
    public string Inner => Token + " inner";
    public string ListName => Token + " list";
    public string Row(int i) => $"{Token} row {i:00}";

    public IntPtr Handle { get; private set; }

    public OwnedFindFiltersWindow()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "wmcp-owned-find-filters" };
        thread.Start();
        // Fail here, naming the fixture, rather than letting every row fail later on a window that
        // is not there yet (or worse, pass a row that only asserts absence).
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException($"{nameof(OwnedFindFiltersWindow)}: the window thread did not create its window within 10 s.");
    }

    public bool Exists => Handle != IntPtr.Zero && IsWindow(Handle);

    private static IntPtr Id(string id) => new(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture));

    private void Pump()
    {
        Handle = CreateWindowExW(0, "STATIC", Title, WS_OVERLAPPEDWINDOW,
            160, 160, 560, 420, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Handle != IntPtr.Zero)
        {
            const uint Button = WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON;
            CreateWindowExW(0, "BUTTON", "", Button, 10, 10, 60, 24, Handle, Id(UnnamedButtonId), IntPtr.Zero, IntPtr.Zero);
            CreateWindowExW(0, "BUTTON", Twin, Button, 80, 10, 200, 24, Handle, Id(TwinAId), IntPtr.Zero, IntPtr.Zero);
            CreateWindowExW(0, "BUTTON", Twin, Button, 290, 10, 200, 24, Handle, Id(TwinBId), IntPtr.Zero, IntPtr.Zero);
            CreateWindowExW(0, "EDIT", "e4 edit text", WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL,
                10, 44, 300, 22, Handle, Id(EditId), IntPtr.Zero, IntPtr.Zero);

            // The group box is the parent WINDOW of the inner button, so UIA's HWND tree puts the
            // button under the group; its namesake outside is a sibling of the group.
            var group = CreateWindowExW(0, "BUTTON", Group, WS_CHILD | WS_VISIBLE | BS_GROUPBOX,
                10, 76, 300, 70, Handle, Id(GroupId), IntPtr.Zero, IntPtr.Zero);
            if (group != IntPtr.Zero)
                CreateWindowExW(0, "BUTTON", Inner, Button, 10, 24, 260, 24, group, Id(InnerInGroupId), IntPtr.Zero, IntPtr.Zero);
            CreateWindowExW(0, "BUTTON", Inner, Button, 320, 100, 200, 24, Handle, Id(InnerOutsideId), IntPtr.Zero, IntPtr.Zero);

            var list = CreateWindowExW(0, "LISTBOX", ListName,
                WS_CHILD | WS_VISIBLE | WS_BORDER | WS_VSCROLL | LBS_NOTIFY | LBS_NOINTEGRALHEIGHT,
                10, 156, 300, 120, Handle, Id(ListId), IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < RowCount; i++)
                SendMessageW(list, LB_ADDSTRING, IntPtr.Zero, Row(i));

            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            // WM_CLOSE destroys a STATIC window but posts no WM_QUIT (OwnedControlsWindow's note).
            if (Handle != IntPtr.Zero && !IsWindow(Handle)) break;
        }
    }

    /// <summary>Closes the window (and its children) and waits until it is really gone.</summary>
    public void Close()
    {
        if (!Exists) return;
        PostMessageW(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < 40 && Exists; i++) Thread.Sleep(50);
        if (Exists) DestroyWindow(Handle);
    }

    public void Dispose()
    {
        Close();
        _ready.Dispose();
    }
}
