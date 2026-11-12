using System.Runtime.InteropServices;
using System.Text;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// E-2: a top-level window owned by this process whose real Win32 / common controls offer the
/// patterns <c>interact_element</c>'s new verbs drive, so their SUCCESS paths can be proven
/// through real UIA instead of only through a fake element:
/// <list type="bullet">
/// <item>a trackbar (RangeValuePattern, range <see cref="SliderMin"/>..<see cref="SliderMax"/>, at <see cref="SliderStart"/>);</item>
/// <item>a writable EDIT, an <c>ES_READONLY</c> EDIT and an <c>ES_PASSWORD</c> EDIT (ValuePattern);</item>
/// <item>a drop-down-list COMBOBOX (Apple, Banana, Cherry; Apple selected) whose ValuePattern accepts any
/// string but only changes for an item's name, matched case-insensitively;</item>
/// <item>a 0x0 push button (no on-screen point, no ScrollItemPattern);</item>
/// <item>a short LISTBOX of <see cref="RowCount"/> rows, so the last row starts scrolled out of view (ScrollItemPattern);</item>
/// <item>a TreeView with a collapsed folder that has a child, and a leaf (ExpandCollapsePattern);</item>
/// <item>a plain push button (none of the four patterns), inside a parent window that has a
/// vertical scroll bar (ScrollPattern), so the scroll_into_view refusal has a real scrollable
/// ancestor to name.</item>
/// </list>
/// Same shape as <see cref="OwnedControlsWindow"/> (a separate class, so that fixture's exact-match
/// counts are untouched): built-in classes, its own message pump, shown without activation at the
/// bottom of the z-order. Every pattern call the tests make is a window message to this process's
/// own controls — nothing is clicked or typed.
/// </summary>
internal sealed class OwnedPatternControlsWindow : IDisposable
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint BS_PUSHBUTTON = 0x0;
    private const uint ES_AUTOHSCROLL = 0x0080;
    private const uint ES_READONLY = 0x0800;
    private const uint ES_PASSWORD = 0x0020;
    private const uint CBS_DROPDOWNLIST = 0x0003;
    private const uint CB_ADDSTRING = 0x0143;
    private const uint CB_SETCURSEL = 0x014E;
    private const uint LBS_NOTIFY = 0x0001;
    private const uint LBS_NOINTEGRALHEIGHT = 0x0100;
    private const uint TVS_HASBUTTONS = 0x0001;
    private const uint TVS_HASLINES = 0x0002;
    private const uint TVS_LINESATROOT = 0x0004;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int SB_VERT = 1;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_USER = 0x0400;
    private const uint TBM_GETPOS = WM_USER;
    private const uint TBM_SETPOS = WM_USER + 5;
    private const uint TBM_SETRANGEMIN = WM_USER + 7;
    private const uint TBM_SETRANGEMAX = WM_USER + 8;
    private const uint LB_ADDSTRING = 0x0180;
    private const uint LB_GETTOPINDEX = 0x018E;
    private const uint TV_FIRST = 0x1100;
    private const uint TVM_GETITEMSTATE = TV_FIRST + 39;
    private const uint TVM_INSERTITEMW = TV_FIRST + 50;
    private const uint TVIF_TEXT = 0x0001;
    private const uint TVIS_EXPANDED = 0x0020;
    private const int ICC_TREEVIEW_CLASSES = 0x0002;
    private const int ICC_BAR_CLASSES = 0x0004;
    private static readonly IntPtr TVI_ROOT = new(-0x10000);
    private static readonly IntPtr TVI_LAST = new(-0x0FFFF);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    public const int SliderMin = 0;
    public const int SliderMax = 100;
    public const int SliderStart = 10;
    public const int RowCount = 40;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct INITCOMMONCONTROLSEX
    {
        public int dwSize;
        public int dwICC;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TVITEMW
    {
        public uint mask;
        public IntPtr hItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public int iSelectedImage;
        public int cChildren;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TVINSERTSTRUCTW
    {
        public IntPtr hParent;
        public IntPtr hInsertAfter;
        public TVITEMW item;
    }

    [DllImport("comctl32.dll")]
    private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern int SetScrollRange(IntPtr hWnd, int bar, int min, int max, bool redraw);

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

    /// <summary>Unique per instance; the window title and every name below start with it.</summary>
    public string Token { get; } = "wmcp-pat-" + Guid.NewGuid().ToString("N");
    public string Title => Token;
    public string PlainButton => Token + " plain button";
    public string ZeroButtonName => Token + " zero button";
    public string Folder => Token + " folder";
    public string FolderChild => Token + " child";
    public string Leaf => Token + " leaf";
    public string Row(int i) => $"{Token} row {i:00}";
    public const string EditStart = "old text";
    public const string ReadOnlyEditText = "locked text";
    public const string PasswordStart = "hunter2-old";
    public static readonly string[] ComboItems = ["Apple", "Banana", "Cherry"];

    public IntPtr Handle { get; private set; }
    public IntPtr Button { get; private set; }
    public IntPtr PasswordEdit { get; private set; }
    /// <summary>
    /// A visible push button with a 0x0 rect: no on-screen point, so scroll_into_view without
    /// ScrollItemPattern is a refusal for it. (A button clipped far below the client area, or one
    /// without WS_VISIBLE, is NOT usable for that: UIA's Win32 proxy reports both IsOffscreen=false
    /// with a non-empty rect.)
    /// </summary>
    public IntPtr ZeroButton { get; private set; }
    public IntPtr Combo { get; private set; }
    public IntPtr Slider { get; private set; }
    public IntPtr Edit { get; private set; }
    public IntPtr ReadOnlyEdit { get; private set; }
    public IntPtr ListBox { get; private set; }
    public IntPtr Tree { get; private set; }
    public IntPtr FolderItem { get; private set; }

    public OwnedPatternControlsWindow()
    {
        var thread = new Thread(Pump) { IsBackground = true, Name = "wmcp-owned-pattern-controls" };
        thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException($"{nameof(OwnedPatternControlsWindow)}: the window thread did not create its window within 10 s.");
    }

    public bool Exists => Handle != IntPtr.Zero && IsWindow(Handle);

    /// <summary>The trackbar's position, read from the control itself.</summary>
    public int SliderPosition => (int)SendMessageW(Slider, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);

    /// <summary>The index of the list box's first visible row.</summary>
    public int ListTopIndex => (int)SendMessageW(ListBox, LB_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Whether the tree's folder node is expanded, read from the control itself.</summary>
    public bool FolderExpanded =>
        ((uint)SendMessageW(Tree, TVM_GETITEMSTATE, FolderItem, new IntPtr(TVIS_EXPANDED)) & TVIS_EXPANDED) != 0;

    public static string TextOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private void Pump()
    {
        var icc = new INITCOMMONCONTROLSEX
        {
            dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC_BAR_CLASSES | ICC_TREEVIEW_CLASSES,
        };
        InitCommonControlsEx(ref icc);

        Handle = CreateWindowExW(0, "STATIC", Title, WS_OVERLAPPEDWINDOW | WS_VSCROLL,
            140, 140, 560, 420, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Handle != IntPtr.Zero)
        {
            // A real, active vertical scroll bar on the parent: UIA's window proxy then offers
            // ScrollPattern on it, which makes it the button's nearest scrollable ancestor.
            SetScrollRange(Handle, SB_VERT, 0, 100, true);

            Button = CreateWindowExW(0, "BUTTON", PlainButton, WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                10, 10, 300, 24, Handle, new IntPtr(201), IntPtr.Zero, IntPtr.Zero);

            Slider = CreateWindowExW(0, "msctls_trackbar32", Token + " slider", WS_CHILD | WS_VISIBLE,
                10, 40, 300, 30, Handle, new IntPtr(202), IntPtr.Zero, IntPtr.Zero);
            SendMessageW(Slider, TBM_SETRANGEMIN, IntPtr.Zero, new IntPtr(SliderMin));
            SendMessageW(Slider, TBM_SETRANGEMAX, new IntPtr(1), new IntPtr(SliderMax));
            SendMessageW(Slider, TBM_SETPOS, new IntPtr(1), new IntPtr(SliderStart));

            Edit = CreateWindowExW(0, "EDIT", EditStart, WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL,
                10, 80, 300, 22, Handle, new IntPtr(203), IntPtr.Zero, IntPtr.Zero);
            ReadOnlyEdit = CreateWindowExW(0, "EDIT", ReadOnlyEditText, WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL | ES_READONLY,
                10, 110, 300, 22, Handle, new IntPtr(204), IntPtr.Zero, IntPtr.Zero);

            ZeroButton = CreateWindowExW(0, "BUTTON", ZeroButtonName, WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                330, 10, 0, 0, Handle, new IntPtr(207), IntPtr.Zero, IntPtr.Zero);
            PasswordEdit = CreateWindowExW(0, "EDIT", PasswordStart, WS_CHILD | WS_VISIBLE | WS_BORDER | ES_AUTOHSCROLL | ES_PASSWORD,
                330, 80, 200, 22, Handle, new IntPtr(208), IntPtr.Zero, IntPtr.Zero);
            Combo = CreateWindowExW(0, "COMBOBOX", Token + " combo", WS_CHILD | WS_VISIBLE | WS_VSCROLL | CBS_DROPDOWNLIST,
                330, 110, 200, 150, Handle, new IntPtr(209), IntPtr.Zero, IntPtr.Zero);
            foreach (var item in ComboItems)
                SendMessageW(Combo, CB_ADDSTRING, IntPtr.Zero, item);
            SendMessageW(Combo, CB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);

            ListBox = CreateWindowExW(0, "LISTBOX", Token + " list",
                WS_CHILD | WS_VISIBLE | WS_BORDER | WS_VSCROLL | LBS_NOTIFY | LBS_NOINTEGRALHEIGHT,
                10, 140, 300, 60, Handle, new IntPtr(205), IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < RowCount; i++)
                SendMessageW(ListBox, LB_ADDSTRING, IntPtr.Zero, Row(i));

            Tree = CreateWindowExW(0, "SysTreeView32", Token + " tree",
                WS_CHILD | WS_VISIBLE | WS_BORDER | TVS_HASBUTTONS | TVS_HASLINES | TVS_LINESATROOT,
                10, 210, 300, 140, Handle, new IntPtr(206), IntPtr.Zero, IntPtr.Zero);
            FolderItem = InsertTreeItem(TVI_ROOT, Folder);
            InsertTreeItem(FolderItem, FolderChild);
            InsertTreeItem(TVI_ROOT, Leaf);

            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
            if (Handle != IntPtr.Zero && !IsWindow(Handle)) break;
        }
    }

    private IntPtr InsertTreeItem(IntPtr parent, string text)
    {
        var textPtr = Marshal.StringToHGlobalUni(text);
        // The union in TVINSERTSTRUCTW is a TVITEMEXW, larger than the TVITEMW written here:
        // a zeroed oversize buffer keeps whatever the control reads past it defined.
        const int size = 256;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.StructureToPtr(new TVINSERTSTRUCTW
            {
                hParent = parent,
                hInsertAfter = TVI_LAST,
                item = new TVITEMW { mask = TVIF_TEXT, pszText = textPtr },
            }, buffer, false);
            return SendMessageW(Tree, TVM_INSERTITEMW, IntPtr.Zero, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(textPtr);
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
