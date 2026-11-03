using System.Collections;
using Marquee.Data;
using Marquee.Shell;
using Microsoft.UI.Xaml;

namespace Marquee.Player;

/// <summary>
/// Functions for x:Bind in the player controls. x:Bind doesn't call a function whose argument is
/// null; the target keeps its old value. So a visibility mustn't hinge on a nullable argument alone:
/// bind to a bool like <see cref="PlayerViewModel.HasMessage"/> instead.
/// </summary>
internal static class Bind
{
    public static Visibility Vis(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisNot(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisAny(IEnumerable? items) => items?.GetEnumerator().MoveNext() == true ? Visibility.Visible : Visibility.Collapsed;

    public static double Opacity(bool visible) => visible ? 1 : 0;

    public static string PlayGlyph(bool playing) => playing ? "" : "";

    public static string FullScreenGlyph(bool fullScreen) => fullScreen ? "" : "";

    public static string CompactGlyph(bool compact) => compact ? "" : "";

    public static string VolumeGlyph(bool muted, double volume) => muted || volume <= 0 ? "" : volume < 34 ? "" : volume < 67 ? "" : "";

    public static string Clock(long ms) => Format.Clock(TimeSpan.FromMilliseconds(ms));

    /// <summary>Unnamed chapters are numbered by start time, since the template doesn't know the index.</summary>
    public static string ChapterTitle(string? title, long startMs) => title ?? $"Chapter at {Clock(startMs)}";

    public static string UpNextHeader(int seconds) => seconds > 0 ? $"UP NEXT · PLAYING IN {seconds}s" : "UP NEXT";

    public static string UpNextTitle(MediaItem? item) =>
        item is null ? "" : Format.Join(Format.EpisodeCode(item), item.Title);
}
