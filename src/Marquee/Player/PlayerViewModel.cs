using CommunityToolkit.Mvvm.ComponentModel;
using Marquee.Data;

namespace Marquee.Player;

/// <summary>What the player controls show. Both control surfaces bind to one instance.</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Subtitle { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<string> Badges { get; set; } = [];
    [ObservableProperty] public partial string EngineText { get; set; } = "";

    [ObservableProperty] public partial double Position { get; set; }
    [ObservableProperty] public partial double Duration { get; set; } = 1;
    [ObservableProperty] public partial double Buffered { get; set; }
    [ObservableProperty] public partial string PositionText { get; set; } = "0:00";
    [ObservableProperty] public partial string DurationText { get; set; } = "0:00";
    [ObservableProperty] public partial string ChapterTitle { get; set; } = "";

    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial bool IsBuffering { get; set; } = true;
    [ObservableProperty] public partial double Volume { get; set; } = 100;
    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial bool IsFullScreen { get; set; }
    [ObservableProperty] public partial bool IsCompact { get; set; }
    [ObservableProperty] public partial bool ControlsVisible { get; set; } = true;

    [ObservableProperty] public partial IReadOnlyList<TrackOption> AudioTracks { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<TrackOption> SubtitleTracks { get; set; } = [];
    [ObservableProperty] public partial int? SelectedAudio { get; set; }
    [ObservableProperty] public partial int? SelectedSubtitle { get; set; }
    [ObservableProperty] public partial bool CanShowSubtitles { get; set; } = true;
    [ObservableProperty] public partial IReadOnlyList<Chapter> Chapters { get; set; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSkip))] public partial string? SkipLabel { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpNext))] public partial MediaItem? UpNext { get; set; }
    [ObservableProperty] public partial int UpNextSeconds { get; set; }

    [ObservableProperty] public partial double Speed { get; set; } = 1;
    [ObservableProperty] public partial int AudioDelayMs { get; set; }
    [ObservableProperty] public partial double SubtitleDelay { get; set; }
    [ObservableProperty] public partial string Statistics { get; set; } = "";
    [ObservableProperty] public partial bool ShowStatistics { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] public partial string? Message { get; set; }

    // Visibility binds to these rather than to the nullable values: x:Bind doesn't call a binding
    // function whose argument is null, so the element would stay visible after the value clears.
    public bool HasSkip => !string.IsNullOrEmpty(SkipLabel);
    public bool HasUpNext => UpNext is not null;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
}

/// <summary>What the control surfaces can ask the player to do.</summary>
public interface IPlayerActions
{
    void TogglePlay();
    void SeekTo(double seconds, bool final);
    void SkipBy(double seconds);
    void SetVolume(double volume);
    void ToggleMute();
    void ToggleFullScreen();
    void ToggleCompact();
    void Close();
    void SelectAudio(int ordinal);
    void SelectSubtitle(int? ordinal);
    void JumpToChapter(Chapter chapter);
    void Skip();
    void PlayUpNext();
    void CancelUpNext();
    void LoadSubtitleFile();
    void SetSpeed(double speed);
    void SetAudioDelay(int milliseconds);
    void SetSubtitleDelay(double seconds);
    void SwitchEngine();
    void ToggleStatistics();
    void PointerActivity();
}
