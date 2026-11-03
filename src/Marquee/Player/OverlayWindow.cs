using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace Marquee.Player;

/// <summary>
/// A borderless, transparent, non-activating window owned by the main window and kept exactly over
/// mpv's video. XAML can't draw on top of mpv's child HWND (airspace), but a separate top-level
/// window composited by DWM can, so the player controls live here while mpv plays.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly IntPtr _hwnd;
    private RectInt32 _bounds;

    public OverlayWindow(IntPtr owner, UIElement content)
    {
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { content } };
        SystemBackdrop = new TransparentBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        _hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, owner);
        SetWindowSubclass(_hwnd, s_subclass, UIntPtr.Zero, UIntPtr.Zero);
        long style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)((style & ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME)) | WS_POPUP));
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)((ex & ~(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_DLGMODALFRAME)) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        int doNotRound = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
        int noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        // The transparent backdrop alone leaves the window opaque; extending DWM's frame over the
        // whole client area makes DWM blend the window's content with what's behind it.
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    public void Place(RectInt32 screenBounds)
    {
        if (screenBounds.Equals(_bounds))
        {
            return;
        }

        _bounds = screenBounds;
        AppWindow.MoveAndResize(screenBounds);
    }

    public void ShowWithoutActivating()
    {
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
        int noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    public void Hide() => ShowWindow(_hwnd, SW_HIDE);

    /// <summary>
    /// No non-client area, whatever the style says. The presenter puts a dialog frame (WS_DLGFRAME)
    /// back after the styles above are stripped, which shrank the client area and drew a white
    /// 3-pixel border around the video.
    /// </summary>
    private static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data) =>
        msg == WM_NCCALCSIZE && wParam != IntPtr.Zero ? IntPtr.Zero : DefSubclassProc(hwnd, msg, wParam, lParam);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    private static readonly SubclassProc s_subclass = OnMessage;
    private const uint WM_NCCALCSIZE = 0x0083;

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_HWNDPARENT = -8;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_POPUP = 0x80000000;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_BORDER = 0x00800000;
    private const long WS_DLGFRAME = 0x00400000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_EX_WINDOWEDGE = 0x00000100;
    private const long WS_EX_CLIENTEDGE = 0x00000200;
    private const long WS_EX_DLGMODALFRAME = 0x00000001;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// A backdrop that is fully transparent, so the window's XAML composites straight over whatever
/// is behind it. Needs a Windows.UI.Composition compositor, which needs a Windows.System
/// dispatcher queue on this thread.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? s_compositor;
    private static object? s_dispatcherQueueController;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        try
        {
            EnsureDispatcherQueue();
            s_compositor ??= new Windows.UI.Composition.Compositor();
            connectedTarget.SystemBackdrop = s_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            Shell.Log.Info("Overlay: transparent backdrop attached");
        }
        catch (Exception ex)
        {
            Shell.Log.Error("Overlay: transparent backdrop failed", ex);
        }
    }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    private static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || s_dispatcherQueueController is not null)
        {
            return;
        }

        var options = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
        int hr = CreateDispatcherQueueController(options, out IntPtr controller);
        Shell.Log.Info($"Overlay: CreateDispatcherQueueController 0x{hr:X8}");
        Marshal.ThrowExceptionForHR(hr);
        s_dispatcherQueueController = MarshalInspectable<object>.FromAbi(controller);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
