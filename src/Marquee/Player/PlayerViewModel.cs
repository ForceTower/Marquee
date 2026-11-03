using CommunityToolkit.Mvvm.ComponentModel;
using Marquee.Data;

#pragma warning disable MVVMTK0045 // Field-based [ObservableProperty]; partial properties need a preview C# version on .NET 9.

namespace Marquee.Player;

/// <summary>What the player controls show. Both control surfaces bind to one instance.</summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private IReadOnlyList<string> _badges = [];
    [ObservableProperty] private string _engineText = "";

    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration = 1;
    [ObservableProperty] private double _buffered;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string _chapterTitle = "";

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering = true;
    [ObservableProperty] private double _volume = 100;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _controlsVisible = true;

    [ObservableProperty] private IReadOnlyList<TrackOption> _audioTracks = [];
    [ObservableProperty] private IReadOnlyList<TrackOption> _subtitleTracks = [];
    [ObservableProperty] private int? _selectedAudio;
    [ObservableProperty] private int? _selectedSubtitle;
    [ObservableProperty] private bool _canShowSubtitles = true;
    [ObservableProperty] private IReadOnlyList<Chapter> _chapters = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSkip))] private string? _skipLabel;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpNext))] private MediaItem? _upNext;
    [ObservableProperty] private int _upNextSeconds;

    [ObservableProperty] private double _speed = 1;
    [ObservableProperty] private int _audioDelayMs;
    [ObservableProperty] private double _subtitleDelay;
    [ObservableProperty] private string _statistics = "";
    [ObservableProperty] private bool _showStatistics;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string? _message;

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
