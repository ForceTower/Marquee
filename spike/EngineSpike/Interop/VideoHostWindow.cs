using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace EngineSpike.Interop;

/// <summary>
/// A plain Win32 child window that mpv renders into via --wid. mpv keeps its own D3D11 swap chain
/// inside it, which is what lets it output HDR; the trade-off is that XAML cannot draw on top of it.
/// </summary>
internal sealed class VideoHostWindow : IDisposable
{
    private const string ClassName = "MarqueeVideoHost";
    private static bool s_registered;

    public IntPtr Handle { get; private set; }

    public VideoHostWindow(IntPtr parent)
    {
        IntPtr instance = GetModuleHandleW(null);
        if (!s_registered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
                hInstance = instance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszClassName = ClassName,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                throw new Win32Exception();
            }

            s_registered = true;
        }

        Handle = CreateWindowExW(0, ClassName, "", WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, parent, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception();
        }
    }

    public void SetBounds(RectInt32 r) =>
        SetWindowPos(Handle, HWND_TOP, r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height), SWP_NOACTIVATE);

    public void Show(bool visible) => ShowWindow(Handle, visible ? SW_SHOWNA : SW_HIDE);

    /// <summary>Gives keyboard focus to mpv's own window inside the host, so mpv's key bindings work.</summary>
    public void FocusChild()
    {
        IntPtr child = GetWindow(Handle, GW_CHILD);
        SetFocus(child != IntPtr.Zero ? child : Handle);
    }

    public static void Focus(IntPtr hwnd) => SetFocus(hwnd);

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;
    private const uint WS_CLIPSIBLINGS = 0x04000000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;
    private const int BLACK_BRUSH = 4;
    private const uint GW_CHILD = 5;
    private static readonly IntPtr IDC_ARROW = 32512;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);
}
