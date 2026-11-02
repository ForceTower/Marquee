using Marquee.Data;

namespace Marquee.Player;

internal static class PlayerLauncher
{
    /// <param name="engine">Force an engine; null lets the planner choose.</param>
    public static void Play(MediaItem item, bool resume, EngineMode? engine = null) =>
        _ = PlayerView.PlayAsync(item, resume, engine);
}
