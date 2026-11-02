using System.Numerics;
using Marquee.Shell;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Marquee.Controls;

/// <summary>
/// Hover for library cards: the whole card (artwork and caption) lifts, and a soft glow in the
/// artwork's own colour fades in behind it. The glow is a composition drop shadow cast by a
/// sprite hidden under the artwork, inset so its square corners never show past the rounded ones.
/// </summary>
internal sealed class CardHover
{
    private const float Inset = 8;

    private readonly FrameworkElement _owner;
    private readonly FrameworkElement _lift;
    private readonly SpriteVisual _sprite;
    private readonly DropShadow _shadow;
    private readonly CompositionColorBrush _brush;
    private readonly Compositor _compositor;

    public CardHover(FrameworkElement owner, FrameworkElement lift, FrameworkElement glowHost)
    {
        _owner = owner;
        _lift = lift;
        _lift.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) };
        _lift.SizeChanged += (_, e) => _lift.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);

        _compositor = ElementCompositionPreview.GetElementVisual(glowHost).Compositor;
        _brush = _compositor.CreateColorBrush(Color.FromArgb(255, 60, 60, 66));
        _shadow = _compositor.CreateDropShadow();
        _shadow.BlurRadius = 40;
        _shadow.Offset = new Vector3(0, 10, 0);
        _shadow.Opacity = 0;
        _shadow.Color = _brush.Color;
        _sprite = _compositor.CreateSpriteVisual();
        _sprite.Brush = _brush;
        _sprite.Shadow = _shadow;
        _sprite.Offset = new Vector3(Inset, Inset, 0);
        ElementCompositionPreview.SetElementChildVisual(glowHost, _sprite);
        glowHost.SizeChanged += (_, e) =>
            _sprite.Size = new Vector2(Math.Max(0, (float)e.NewSize.Width - 2 * Inset), Math.Max(0, (float)e.NewSize.Height - 2 * Inset));
    }

    public void SetColor(Color color)
    {
        var glow = Vivid(color);
        _brush.Color = glow;
        _shadow.Color = glow;
    }

    public void Enter()
    {
        Canvas.SetZIndex(_owner, 10);
        _lift.Scale = new Vector3(1.05f, 1.05f, 1);
        Fade(0.85f);
    }

    public void Exit()
    {
        Canvas.SetZIndex(_owner, 0);
        _lift.Scale = Vector3.One;
        Fade(0);
    }

    private void Fade(float to)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, to);
        animation.Duration = TimeSpan.FromMilliseconds(to > 0 ? 200 : 140);
        _shadow.StartAnimation(nameof(DropShadow.Opacity), animation);
    }

    /// <summary>Edge colours are often muted; lift them so the glow reads as light, not as a dark smudge.</summary>
    private static Color Vivid(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max < 1)
        {
            return Color.FromArgb(255, 70, 70, 76);
        }

        double scale = Math.Clamp(200 / max, 1, 3);
        return Color.FromArgb(255, (byte)Math.Min(255, c.R * scale), (byte)Math.Min(255, c.G * scale), (byte)Math.Min(255, c.B * scale));
    }

    /// <summary>A card's glow colour: Plex's UltraBlur edge colours, else the artwork's average colour.</summary>
    public static async Task<Color?> ColorForAsync(string? tint, string? imageReference, int width, int height)
    {
        if (tint?.Split(',') is { Length: 4 } corners)
        {
            var colors = corners.Select(Theme.Parse).ToList();
            return Color.FromArgb(255,
                (byte)colors.Average(x => x.R), (byte)colors.Average(x => x.G), (byte)colors.Average(x => x.B));
        }

        return imageReference is null ? null : await Imaging.ImageColors.AverageAsync(imageReference, width, height);
    }
}
