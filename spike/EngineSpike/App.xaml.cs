using Microsoft.UI.Xaml;

namespace EngineSpike;

public partial class App : Application
{
    private MainWindow? _window;
    private AtmosLabWindow? _labWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = SpikeOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        if (options is { ExtractTrack: int track, Path: { } path, ExtractOutput: { } output })
        {
            string result;
            try
            {
                result = Matroska.TrackExtractor.Run(path, track, options.Start ?? TimeSpan.Zero, options.ExtractLength, output);
            }
            catch (Exception ex)
            {
                result = $"extract failed: {ex}";
            }

            File.WriteAllText(output + ".log", result);
            Exit();
            return;
        }

        if (options is { DvInfoOutput: { } dvInfo, Path: { } file })
        {
            string result;
            try
            {
                result = Matroska.DolbyVisionProbe.Describe(file);
            }
            catch (Exception ex)
            {
                result = $"failed: {ex.Message}";
            }

            File.WriteAllText(dvInfo, result);
            Exit();
            return;
        }

        if (options is { AtmosLab: { } atmos, Bed: { } bed })
        {
            _labWindow = new AtmosLabWindow(atmos, bed);
            _labWindow.Activate();
            return;
        }

        _window = new MainWindow(options);
        _window.Activate();
    }
}
