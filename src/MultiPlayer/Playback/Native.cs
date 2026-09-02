using System.Runtime.InteropServices;

namespace MultiPlayer.Playback;

/// <summary>Win32 surface plumbing. Video surfaces are real child HWNDs so they can be
/// re-parented between windows (and between displays) without touching playback.</summary>
internal static class Native
{
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_POPUP = 0x80000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_FRAMECHANGED = 0x0020;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int MONITORINFOF_PRIMARY = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumDelegate proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vKey);

    private const int VK_SHIFT = 0x10;

    /// <summary>Read from Win32 rather than WPF: with no focused element, WPF's own
    /// modifier state is never updated.</summary>
    public static bool IsShiftDown() => (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? module);

    public const int BLACK_BRUSH = 4;

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int index);

    // --- surface window class -------------------------------------------------

    private const string SurfaceClass = "MultiPlayerVideoSurface";
    private static bool _registered;
    // Held in a static so the CLR never collects the thunk the OS is calling into.
    private static WndProcDelegate? _wndProc;

    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_PARENTNOTIFY = 0x0210;
    public const int MA_NOACTIVATE = 3;

    /// <summary>Raised with the surface's own HWND when that video is clicked.</summary>
    public static event Action<IntPtr>? SurfaceClicked;

    /// <summary>
    /// Refuses activation so that clicking a video never pulls keyboard focus off the
    /// window — the whole app is driven from the 10-key, and losing focus to a video
    /// surface would silently break every shortcut — while still reporting the click.
    /// <para>
    /// LibVLC renders into its own child window inside ours, and that child consumes the
    /// button message; mouse messages do not bubble. WM_PARENTNOTIFY is the exception:
    /// Windows tells the parent chain about a button press in a child, which is how a
    /// click on the picture itself reaches us.
    /// </para>
    /// </summary>
    private static IntPtr SurfaceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;

            case WM_LBUTTONDOWN:
                SurfaceClicked?.Invoke(hWnd);
                break;

            case WM_PARENTNOTIFY:
                if ((wParam.ToInt64() & 0xFFFF) == WM_LBUTTONDOWN) SurfaceClicked?.Invoke(hWnd);
                break;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void EnsureClass()
    {
        if (_registered) return;
        _wndProc = SurfaceWndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            hbrBackground = GetStockObject(BLACK_BRUSH), // never flash system grey behind a video
            lpszClassName = SurfaceClass,
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            // 1410 = ERROR_CLASS_ALREADY_EXISTS, fine on a second call.
            if (err != 1410) throw new InvalidOperationException($"RegisterClassEx failed ({err}).");
        }
        _registered = true;
    }

    /// <summary>Creates an empty black child window suitable for handing to LibVLC.</summary>
    public static IntPtr CreateSurface(IntPtr parent, bool visible = false)
    {
        EnsureClass();
        int style = WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | (visible ? WS_VISIBLE : 0);
        var hwnd = CreateWindowEx(
            0, SurfaceClass, null, style,
            0, 0, 16, 9, parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
        return hwnd;
    }

    /// <summary>
    /// A hidden top-level window that owns every surface not currently on screen.
    /// Surfaces must always have a parent, and parking them here means tearing down a
    /// view never takes a live decoder's window down with it.
    /// </summary>
    public static IntPtr CreateParkingWindow()
    {
        EnsureClass();
        var hwnd = CreateWindowEx(
            WS_EX_TOOLWINDOW, SurfaceClass, "MultiPlayer.Parking", unchecked((int)WS_POPUP),
            -32000, -32000, 8, 8, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"parking CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");
        return hwnd;
    }

    /// <summary>The display a window currently sits on, or null if it has no handle yet.</summary>
    public static MonitorInfo? MonitorForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var h = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (h == IntPtr.Zero) return null;
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(h, ref mi)) return null;
        return new MonitorInfo(mi.szDevice, mi.rcMonitor.Left, mi.rcMonitor.Top,
            mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
            (mi.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi))
            {
                list.Add(new MonitorInfo(
                    mi.szDevice,
                    mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);

        list.Sort((a, b) => a.Primary == b.Primary ? a.X.CompareTo(b.X) : (a.Primary ? -1 : 1));
        return list;
    }
}

/// <summary>A display, in physical pixels on the virtual desktop.</summary>
public sealed record MonitorInfo(string Device, int X, int Y, int Width, int Height, bool Primary)
{
    public string Label
    {
        get
        {
            var n = Device.Replace(@"\.\DISPLAY", "Display ");
            return $"{n} — {Width}×{Height}{(Primary ? " (primary)" : "")}";
        }
    }
}
