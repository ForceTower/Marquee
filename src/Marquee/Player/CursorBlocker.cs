using System.Runtime.InteropServices;
using Marquee.Shell;
using Windows.Graphics;

namespace Marquee.Player;

/// <summary>
/// An invisible window laid over the video while the controls are hidden, there only to hide the
/// mouse cursor. XAML can't do it: WinUI resolves cursors on its own input thread, which re-applies
/// the arrow for a pointer that isn't moving whatever cursor XAML asks for. This plain window has
/// no class cursor, so Windows hides the pointer as soon as the window appears under it; the first
/// movement, click or wheel turn reports activity and the player takes the window down again.
/// </summary>
internal sealed class CursorBlocker : IDisposable
{
    private const string ClassName = "MarqueeCursorBlocker";
    private static readonly WndProc s_wndProc = WindowProc;
    private static readonly Dictionary<IntPtr, CursorBlocker> s_blockers = [];
    private static bool s_registered;

    private readonly IntPtr _hwnd;
    private POINT _shownAt;

    public CursorBlocker(IntPtr owner)
    {
        if (!s_registered)
        {
            var wc = new WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                Instance = GetModuleHandle(null),
                ClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
            }

            s_registered = true;
        }

        _hwnd = CreateWindowEx(WS_EX_NOREDIRECTIONBITMAP | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, ClassName, "",
            WS_POPUP, 0, 0, 0, 0, owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        }

        s_blockers[_hwnd] = this;
        int doNotRound = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
        int noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    /// <summary>Movement, a click or the wheel over the hidden-cursor area.</summary>
    public event Action? Activity;

    /// <summary>The primary button went down over the hidden-cursor area (raised after <see cref="Activity"/>).</summary>
    public event Action? Pressed;

    public void Show(RectInt32 screenBounds)
    {
        GetCursorPos(out _shownAt);
        SetWindowPos(_hwnd, HWND_TOP, screenBounds.X, screenBounds.Y, screenBounds.Width, screenBounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Hide() => ShowWindow(_hwnd, SW_HIDE);

    public void Dispose()
    {
        s_blockers.Remove(_hwnd);
        DestroyWindow(_hwnd);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!s_blockers.TryGetValue(hwnd, out var blocker))
        {
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(IntPtr.Zero);
                return 1;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_MOUSEMOVE:
                // Windows sends a move without movement when the window appears under the pointer.
                if (GetCursorPos(out var at) && (at.X != blocker._shownAt.X || at.Y != blocker._shownAt.Y))
                {
                    blocker.RaiseActivity();
                }

                return 0;
            case WM_LBUTTONDOWN:
                blocker.RaiseActivity();
                Log.Guard("Cursor blocker press", () => blocker.Pressed?.Invoke());
                return 0;
            case WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN or WM_MOUSEWHEEL or WM_MOUSEHWHEEL:
                blocker.RaiseActivity();
                return 0;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private void RaiseActivity() => Log.Guard("Cursor blocker activity", () => Activity?.Invoke());

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private const int MA_NOACTIVATE = 3;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
}
