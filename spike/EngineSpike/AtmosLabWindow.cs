using EngineSpike.SpatialAudio;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;

namespace EngineSpike;

/// <summary>
/// Listening proof for TrueHD Atmos objects: plays a truehdd-decoded Atmos presentation and its
/// 7.1 downmix through Windows Spatial Sound, switchable instantly, with a top-down view of where
/// each object is (colour = height, size = level).
/// </summary>
public sealed class AtmosLabWindow : Window
{
    private const double Room = 440;
    private const double Margin = 40;

    private readonly AtmosLabRenderer? _renderer;
    private readonly Canvas _canvas = new() { Width = Room + 2 * Margin, Height = Room + 2 * Margin };
    private readonly TextBlock _mode = new() { FontSize = 30, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _position = new() { FontSize = 18, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _info = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherQueueTimer _timer;
    private string? _loggedInfo;

    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Marquee", "EngineSpike", "atmos-lab.log");

    public AtmosLabWindow(string atmosPath, string bedPath)
    {
        Title = "Marquee · Atmos lab";
        AppWindow.Resize(new SizeInt32(1100, 760));

        var help = new TextBlock
        {
            Text = "A  switch Atmos / 7.1   ·   T  position test   ·   Space  play / pause   ·   ← →  10 s   ·   Home  restart",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190)),
        };
        var side = new StackPanel { Spacing = 14, Margin = new Thickness(24), Width = 520 };
        side.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(atmosPath), FontSize = 14, Foreground = new SolidColorBrush(Colors.Gray) });
        side.Children.Add(_mode);
        side.Children.Add(_position);
        side.Children.Add(help);
        side.Children.Add(_info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Button("Switch Atmos / 7.1", r => r.AtmosMode = !r.AtmosMode));
        buttons.Children.Add(Button("Play / Pause", r => r.Playing = !r.Playing));
        buttons.Children.Add(Button("Restart", r => r.Seek(TimeSpan.Zero)));
        buttons.Children.Add(Button("Position test (T)", r => r.TestMode = !r.TestMode));
        side.Children.Insert(3, buttons);

        var grid = new Grid { Background = new SolidColorBrush(Color.FromArgb(255, 18, 18, 18)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(_canvas);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);

        // Shortcuts need keyboard focus somewhere in the window; nothing else here is focusable.
        var root = new ContentControl
        {
            Content = grid,
            IsTabStop = true,
            RequestedTheme = ElementTheme.Dark,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        AddKey(root, VirtualKey.A, r => r.AtmosMode = !r.AtmosMode);
        AddKey(root, VirtualKey.T, r => r.TestMode = !r.TestMode);
        AddKey(root, VirtualKey.Space, r => r.Playing = !r.Playing);
        AddKey(root, VirtualKey.Left, r => r.Seek(PositionTime() - TimeSpan.FromSeconds(10)));
        AddKey(root, VirtualKey.Right, r => r.Seek(PositionTime() + TimeSpan.FromSeconds(10)));
        AddKey(root, VirtualKey.Home, r => r.Seek(TimeSpan.Zero));
        Content = root;
        Activated += (_, _) => root.Focus(FocusState.Programmatic);

        try
        {
            var atmos = DamfPresentation.Load(atmosPath);
            var bed = CafAudio.Load(bedPath);
            _renderer = new AtmosLabRenderer(atmos, bed);
            _renderer.Failed += message => DispatcherQueue.TryEnqueue(() => _info.Text = "Spatial audio failed: " + message);
        }
        catch (Exception ex)
        {
            _info.Text = "Couldn't load: " + ex.Message;
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(66);
        _timer.Tick += (_, _) => Redraw();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _renderer?.Dispose();
        };
    }

    private TimeSpan PositionTime() => _renderer is null ? TimeSpan.Zero : TimeSpan.FromSeconds(_renderer.Position / (double)_renderer.SampleRate);

    private void AddKey(UIElement target, VirtualKey key, Action<AtmosLabRenderer> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, e) =>
        {
            if (_renderer is not null)
            {
                action(_renderer);
            }

            e.Handled = true;
        };
        target.KeyboardAccelerators.Add(accelerator);
    }

    private Button Button(string text, Action<AtmosLabRenderer> action)
    {
        var button = new Button { Content = text, AllowFocusOnInteraction = false };
        button.Click += (_, _) =>
        {
            if (_renderer is not null)
            {
                action(_renderer);
            }
        };
        return button;
    }

    private void Redraw()
    {
        if (_renderer is null)
        {
            return;
        }

        bool atmos = _renderer.AtmosMode;
        bool test = _renderer.TestMode;
        _mode.Text = test ? $"POSITION TEST · {_renderer.TestPhase}"
            : atmos ? "ATMOS · objects with positions" : "7.1 · Dolby's downmix (what ffmpeg/mpv play)";
        _mode.TextWrapping = TextWrapping.Wrap;
        _mode.Foreground = new SolidColorBrush(test ? Colors.LightGreen : atmos ? Color.FromArgb(255, 255, 160, 60) : Color.FromArgb(255, 110, 170, 230));
        var total = TimeSpan.FromSeconds(_renderer.TotalFrames / (double)_renderer.SampleRate);
        _position.Text = $"{PositionTime():mm\\:ss\\.f} / {total:mm\\:ss}  {(_renderer.Playing ? "▶" : "❚❚")}";
        if (!_info.Text.StartsWith("Spatial audio failed") && !_info.Text.StartsWith("Couldn't"))
        {
            _info.Text = _renderer.Info;
        }

        if (_info.Text != _loggedInfo)
        {
            _loggedInfo = _info.Text;
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {_loggedInfo}{Environment.NewLine}");
        }

        _canvas.Children.Clear();
        var room = new Rectangle
        {
            Width = Room,
            Height = Room,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 70, 70, 70)),
            StrokeThickness = 2,
        };
        Place(room, Margin, Margin);
        AddLabel("FRONT", Margin + Room / 2 - 22, 12, Colors.Gray);
        AddLabel("BACK", Margin + Room / 2 - 18, Margin + Room + 8, Colors.Gray);
        var listener = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Colors.White) };
        Place(listener, Margin + Room / 2 - 6, Margin + Room / 2 - 6);

        if (test)
        {
            DrawSource(_renderer.TestPosition, -6, "test noise", true);
        }
        else if (atmos)
        {
            foreach (var element in _renderer.Elements)
            {
                var p = element.StaticType == AudioObjectType.LowFrequency ? (0f, 0.6f, 0f) : element.Current;
                DrawSource(p, element.LevelDb, element.Element.BedChannel ?? element.Element.Id.ToString(), element.Active);
            }
        }
        else
        {
            string[] names = ["L", "R", "C", "LFE", "Ls", "Rs", "Lb", "Rb"];
            for (int i = 0; i < names.Length; i++)
            {
                DrawSource(AtmosLabRenderer.BedPositions[i], _renderer.BedLevelsDb[i], names[i], true);
            }
        }
    }

    private void DrawSource((float X, float Y, float Z) p, float levelDb, string label, bool active)
    {
        double loudness = Math.Clamp((levelDb + 60) / 60, 0, 1);
        double radius = 7 + loudness * 24;
        double cx = Margin + (p.X + 1) / 2 * Room;
        double cy = Margin + (1 - p.Y) / 2 * Room;
        byte r = (byte)(70 + (255 - 70) * p.Z), g = (byte)(130 + (140 - 130) * p.Z), b = (byte)(180 - 180 * p.Z);
        bool audible = active && levelDb > -70;
        var circle = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = audible ? new SolidColorBrush(Color.FromArgb(210, r, g, b)) : null,
            Stroke = new SolidColorBrush(audible ? Color.FromArgb(255, r, g, b) : Color.FromArgb(255, 80, 80, 80)),
            StrokeThickness = 2,
        };
        Place(circle, cx - radius, cy - radius);
        string height = p.Z > 0.05 ? $" ↑{p.Z:0.0}" : "";
        AddLabel(label + height, cx + radius + 3, cy - 9, audible ? Colors.White : Colors.Gray);
    }

    private void AddLabel(string text, double x, double y, Color color)
    {
        var label = new TextBlock { Text = text, FontSize = 12, Foreground = new SolidColorBrush(color) };
        Place(label, x, y);
    }

    private void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _canvas.Children.Add(element);
    }
}
