using Marquee.Data;
using Marquee.Imaging;
using Marquee.Library;
using Marquee.Playback.Interop;
using Marquee.Plex;
using Marquee.Shell;
using Microsoft.UI.Xaml;

namespace Marquee;

internal sealed class AppServices
{
    public AppServices()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Marquee");
        Db = new LibraryDatabase(Path.Combine(DataDirectory, "library.db"));
        Settings = new SettingsStore(Db);
        Plex = new PlexService(Db);
        Images = new ImageCache(Plex, Path.Combine(DataDirectory, "cache", "images"));
        Sync = new LibrarySync(Db, Plex);
    }

    public string DataDirectory { get; }
    public LibraryDatabase Db { get; }
    public SettingsStore Settings { get; }
    public PlexService Plex { get; }
    public ImageCache Images { get; }
    public LibrarySync Sync { get; }
}

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Error("Unhandled exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception (domain)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    internal static AppServices Services { get; private set; } = null!;

    internal static MainWindow Window => ((App)Current)._window!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Info($"Marquee {Plex.PlexClientIdentity.Version} starting");
        try
        {
            Services = new AppServices();
            Log.Info($"Store codec runtime: {StoreCodecRuntime.Preload()}");
            Theme.ApplyAccent(Services.Settings.Current.Accent);

            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            throw;
        }

        _ = Services.Sync.SyncAllAsync();
    }
}
