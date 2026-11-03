using System.Runtime.InteropServices;
using Marquee.Data;
using Marquee.Shell;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Marquee.Player;

/// <summary>
/// Turns the main window into the mini player and back. The mini player is the picture alone: no
/// title bar, rounded corners, always on top, sized to the video's aspect ratio and held to it while
/// resizing, dragged from anywhere on the picture, and back where it was last time. Windows'
/// CompactOverlay presenter always keeps a title bar, so this reshapes the normal presenter instead.
/// </summary>
internal sealed class MiniPlayer
{
    private const double MinWidthDip = 240;
    private const double MinShortSideDip = 180;
    private const double MaxStartWidthDip = 640;
    private const double MarginDip = 16;
    private static readonly UIntPtr SubclassId = 1;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _window;
    private readonly SubclassProc _subclass;
    private Saved? _saved;
    private double _aspect = 16 / 9.0;
    private bool _dragging;
    private POINT _dragFrom;
    private PointInt32 _dragOrigin;
    private long _lastPress;
    private POINT _lastPressAt;

    public MiniPlayer(IntPtr hwnd, AppWindow window)
    {
        _hwnd = hwnd;
        _window = window;
        _subclass = OnMessage;
    }

    /// <summary>The second press of a double-click on the picture.</summary>
    public event Action? DoubleClicked;

    /// <param name="aspect">The video's width over height, or 0 if it isn't known yet (see <see cref="FitTo"/>).</param>
    public void Enter(double aspect)
    {
        if (_saved is not null)
        {
            return;
        }

        if (_window.Presenter is not OverlappedPresenter)
        {
            _window.SetPresenter(AppWindowPresenterKind.Default);
        }

        var presenter = (OverlappedPresenter)_window.Presenter;
        bool maximized = presenter.State == OverlappedPresenterState.Maximized;
        if (maximized)
        {
            presenter.Restore();
        }

        _saved = new Saved(
            new RectInt32(_window.Position.X, _window.Position.Y, _window.Size.Width, _window.Size.Height),
            maximized, presenter.PreferredMinimumWidth, presenter.PreferredMinimumHeight);
        _aspect = aspect > 0 ? aspect : 16 / 9.0;

        presenter.PreferredMinimumWidth = null;
        presenter.PreferredMinimumHeight = null;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsAlwaysOnTop = true;
        SetCorners(DWMWCP_ROUND);
        SetWindowSubclass(_hwnd, _subclass, SubclassId, UIntPtr.Zero);
        PlacePicture(StartBounds());
    }

    /// <summary>Puts the window back as it was, and remembers where the mini player was.</summary>
    public void Exit()
    {
        if (_saved is not { } saved)
        {
            return;
        }

        var picture = Picture();
        App.Services.Settings.Update(s => s.MiniPlayer = new MiniPlayerPlacement(picture.X, picture.Y, picture.Width));
        StopDrag();
        _saved = null;
        RemoveWindowSubclass(_hwnd, _subclass, SubclassId);
        SetCorners(DWMWCP_DEFAULT);
        if (_window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.SetBorderAndTitleBar(true, true);
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            _window.MoveAndResize(saved.Bounds);
            presenter.PreferredMinimumWidth = saved.MinWidth;
            presenter.PreferredMinimumHeight = saved.MinHeight;
            if (saved.Maximized)
            {
                presenter.Maximize();
            }
        }
    }

    /// <summary>Reshapes the mini player to a new aspect ratio, keeping its width and the screen edge it's nearer.</summary>
    public void FitTo(double aspect)
    {
        if (_saved is null || _dragging || aspect <= 0 || Math.Abs(aspect / _aspect - 1) < 0.005)
        {
            return;
        }

        _aspect = aspect;
        var picture = Picture();
        int height = (int)Math.Round(picture.Width / aspect);
        var work = DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Nearest).WorkArea;
        bool lowerHalf = picture.Y + picture.Height / 2 > work.Y + work.Height / 2;
        PlacePicture(new RectInt32(picture.X, lowerHalf ? picture.Y + picture.Height - height : picture.Y, picture.Width, height));
    }

    /// <summary>
    /// The primary button went down on the picture: the window follows the pointer until it's
    /// released. The pointer is polled each frame rather than captured, because the press can land
    /// in any of three windows (the main one, mpv's overlay, the cursor blocker). Windows' own move
    /// loop isn't used either: it would snap the mini player to half the screen at an edge.
    /// </summary>
    public void BeginDrag()
    {
        if (_saved is null || !GetCursorPos(out var at))
        {
            return;
        }

        long now = Environment.TickCount64;
        bool second = _lastPress > 0 && now - _lastPress <= GetDoubleClickTime() &&
                      Math.Abs(at.X - _lastPressAt.X) * 2 <= GetSystemMetrics(SM_CXDOUBLECLK) &&
                      Math.Abs(at.Y - _lastPressAt.Y) * 2 <= GetSystemMetrics(SM_CYDOUBLECLK);
        _lastPress = second ? 0 : now;
        _lastPressAt = at;
        if (second)
        {
            StopDrag();
            DoubleClicked?.Invoke();
            return;
        }

        _dragFrom = at;
        _dragOrigin = _window.Position;
        if (!_dragging)
        {
            _dragging = true;
            CompositionTarget.Rendering += OnDragFrame;
        }
    }

    private void OnDragFrame(object? sender, object e)
    {
        if (_saved is null || !PrimaryButtonDown() || !GetCursorPos(out var at))
        {
            StopDrag();
            return;
        }

        var to = new PointInt32(_dragOrigin.X + at.X - _dragFrom.X, _dragOrigin.Y + at.Y - _dragFrom.Y);
        if (to.X != _window.Position.X || to.Y != _window.Position.Y)
        {
            _window.Move(to);
        }
    }

    private void StopDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            CompositionTarget.Rendering -= OnDragFrame;
        }
    }

    private static bool PrimaryButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON) & 0x8000) != 0;

    /// <summary>Where it was last time, if that's still on a screen; else the bottom-right corner of this one.</summary>
    private RectInt32 StartBounds()
    {
        if (App.Services.Settings.Current.MiniPlayer is { Width: > 0 } last)
        {
            var picture = new RectInt32(last.X, last.Y, last.Width, (int)Math.Round(last.Width / _aspect));
            if (DisplayArea.GetFromRect(picture, DisplayAreaFallback.None) is { } display)
            {
                var area = display.WorkArea;
                return picture with
                {
                    X = Math.Clamp(picture.X, area.X, Math.Max(area.X, area.X + area.Width - picture.Width)),
                    Y = Math.Clamp(picture.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - picture.Height)),
                };
            }
        }

        double scale = GetDpiForWindow(_hwnd) / 96.0;
        var work = DisplayArea.GetFromWindowId(_window.Id, DisplayAreaFallback.Primary).WorkArea;
        int width = (int)Math.Round(Math.Max(MinWidth(), Math.Min(work.Width * 0.25, MaxStartWidthDip * scale)));
        int height = (int)Math.Round(width / _aspect);
        if (height > work.Height / 2)
        {
            height = work.Height / 2;
            width = (int)Math.Round(height * _aspect);
        }

        int margin = (int)Math.Round(MarginDip * scale);
        return new RectInt32(work.X + work.Width - width - margin, work.Y + work.Height - height - margin, width, height);
    }

    /// <summary>The smallest picture width: 240 DIPs across and 180 on the short side.</summary>
    private double MinWidth() => Math.Max(MinWidthDip, MinShortSideDip * _aspect) * GetDpiForWindow(_hwnd) / 96.0;

    /// <summary>The client area (all picture in the mini player) in screen pixels.</summary>
    private RectInt32 Picture()
    {
        GetClientRect(_hwnd, out var client);
        var origin = new POINT();
        ClientToScreen(_hwnd, ref origin);
        return new RectInt32(origin.X, origin.Y, client.Right, client.Bottom);
    }

    /// <summary>The window's frame around the client area (invisible resize borders, mostly).</summary>
    private (int Left, int Top, int Right, int Bottom) Frame()
    {
        GetWindowRect(_hwnd, out var window);
        var picture = Picture();
        return (picture.X - window.Left, picture.Y - window.Top,
            window.Right - picture.X - picture.Width, window.Bottom - picture.Y - picture.Height);
    }

    private void PlacePicture(RectInt32 picture)
    {
        var (left, top, right, bottom) = Frame();
        _window.MoveAndResize(new RectInt32(picture.X - left, picture.Y - top, picture.Width + left + right, picture.Height + top + bottom));
    }

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_SIZING && _saved is not null)
        {
            Log.Guard("Mini player sizing", () =>
            {
                var rect = Marshal.PtrToStructure<RECT>(lParam);
                KeepAspect(ref rect, (int)wParam);
                Marshal.StructureToPtr(rect, lParam, false);
            });
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Holds the picture to the video's aspect ratio while the user drags an edge or a corner. A
    /// corner follows whichever side the pointer has stretched more; the dragged edges move and the
    /// opposite ones stay put.
    /// </summary>
    private void KeepAspect(ref RECT rect, int edge)
    {
        var (left, top, right, bottom) = Frame();
        int frameWidth = left + right;
        int frameHeight = top + bottom;
        double width = rect.Right - rect.Left - frameWidth;
        double height = rect.Bottom - rect.Top - frameHeight;
        bool byHeight = edge is WMSZ_TOP or WMSZ_BOTTOM;
        if (edge is WMSZ_TOPLEFT or WMSZ_TOPRIGHT or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT)
        {
            var current = Picture();
            byHeight = Math.Abs(height / Math.Max(1, current.Height) - 1) > Math.Abs(width / Math.Max(1, current.Width) - 1);
        }

        if (byHeight)
        {
            width = height * _aspect;
        }

        width = Math.Max(width, MinWidth());
        int outerWidth = (int)Math.Round(width) + frameWidth;
        int outerHeight = (int)Math.Round(width / _aspect) + frameHeight;
        if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT)
        {
            rect.Left = rect.Right - outerWidth;
        }
        else
        {
            rect.Right = rect.Left + outerWidth;
        }

        if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT)
        {
            rect.Top = rect.Bottom - outerHeight;
        }
        else
        {
            rect.Bottom = rect.Top + outerHeight;
        }
    }

    private void SetCorners(int preference) => DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

    private sealed record Saved(RectInt32 Bounds, bool Maximized, int? MinWidth, int? MinHeight);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint WM_SIZING = 0x0214;
    private const int WMSZ_LEFT = 1;
    private const int WMSZ_RIGHT = 2;
    private const int WMSZ_TOP = 3;
    private const int WMSZ_TOPLEFT = 4;
    private const int WMSZ_TOPRIGHT = 5;
    private const int WMSZ_BOTTOM = 6;
    private const int WMSZ_BOTTOMLEFT = 7;
    private const int WMSZ_BOTTOMRIGHT = 8;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_ROUND = 2;
    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;
    private const int SM_SWAPBUTTON = 23;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
