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
                result = TrackExtractor.Run(path, track, options.Start ?? TimeSpan.Zero, options.ExtractLength, output);
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
                result = DolbyVisionProbe.Describe(file);
            }
            catch (Exception ex)
            {
                result = $"failed: {ex.Message}";
            }

            File.WriteAllText(dvInfo, result);
            Exit();
            return;
        }

        if (options.DisplayInfoOutput is { } displayInfo)
        {
            string result;
            try
            {
                result = DisplayControl.Describe();
            }
            catch (Exception ex)
            {
                result = $"failed: {ex}";
            }

            File.WriteAllText(displayInfo, result);
            Exit();
            return;
        }

        if (options.SetHdr is { } hdr)
        {
            DisplayControl.SetHdr(DisplayControl.Targets().First(), hdr == "on");
            Exit();
            return;
        }

        if (options.DisplayTestOutput is { } displayTest)
        {
            _ = Task.Run(() => File.WriteAllText(displayTest, RunDisplayTest()))
                .ContinueWith(_ => Exit(), TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        if (options is { SubtitleTestFolder: { } folder, Path: { } media })
        {
            _ = RunSubtitleTestAsync(media, folder, options.Start ?? TimeSpan.FromMinutes(10), options.SubtitleTrack, options.SubtitleReserve);
            return;
        }

        if (options is { PreviewTestFolder: { } previews, Path: { } video })
        {
            _ = RunPreviewTestAsync(video, previews);
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

    /// <summary>Switches the primary monitor for 23.976 fps and back, then HDR off and on, reading the state after each step.</summary>
    private static string RunDisplayTest()
    {
        var log = new System.Text.StringBuilder();
        void Note(string line) => log.AppendLine($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        try
        {
            var target = DisplayControl.Targets().First();
            string State() => $"{DisplayControl.CurrentMode(target)} · {DisplayControl.Hdr(target)}";
            Note($"{target.GdiName} '{target.FriendlyName}': {State()}");

            var mode = DisplayControl.CurrentMode(target)!.Value;
            if (DisplayControl.PickFrequency(23.976, mode, DisplayControl.Frequencies(target, mode)) is int frequency)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                int result = DisplayControl.SetFrequency(target, mode, frequency);
                Note($"switch to {frequency}: result {result} in {timer.ElapsedMilliseconds} ms");
                Thread.Sleep(4000);
                Note($"now {State()}");
                timer.Restart();
                result = DisplayControl.RestoreMode(target);
                Note($"restore: result {result} in {timer.ElapsedMilliseconds} ms");
                Thread.Sleep(3000);
                Note($"now {State()}");
            }

            if (DisplayControl.Hdr(target) is { Supported: true, Enabled: true })
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                bool done = DisplayControl.SetHdr(target, false);
                Note($"HDR off: {done} in {timer.ElapsedMilliseconds} ms");
                Thread.Sleep(4000);
                Note($"now {State()}");
                timer.Restart();
                done = DisplayControl.SetHdr(target, true);
                Note($"HDR on: {done} in {timer.ElapsedMilliseconds} ms");
                Thread.Sleep(3000);
                Note($"now {State()}");
            }
        }
        catch (Exception ex)
        {
            Note($"failed: {ex}");
        }

        return log.ToString();
    }

    private async Task RunSubtitleTestAsync(string media, string folder, TimeSpan at, int? track, int reserve)
    {
        string result;
        try
        {
            result = await Marquee.Playback.Subtitles.SubtitleProbe.RunAsync(media, folder, at, track, reserve);
        }
        catch (Exception ex)
        {
            result = $"failed: {ex}";
        }

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "subtitles.txt"), result);
        Exit();
    }

    private async Task RunPreviewTestAsync(string media, string folder)
    {
        try
        {
            await Task.Run(() => Marquee.Playback.Previews.ThumbnailProbe.RunAsync(media, folder));
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "report.txt"), $"failed: {ex}");
        }

        Exit();
    }
}
