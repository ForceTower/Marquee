using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace EngineSpike;

/// <summary>
/// Mirrors everything shown in the status pane to %LOCALAPPDATA%\Marquee\EngineSpike\spike.log,
/// so a run can be inspected afterwards without watching the window.
/// </summary>
internal sealed class DiagLog
{
    private readonly TextBlock _infoText;
    private readonly TextBlock _logText;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _fileLock = new();

    public string Directory { get; }
    public string SpikeLogPath { get; }
    public string MpvLogPath { get; }

    public DiagLog(TextBlock infoText, TextBlock logText, DispatcherQueue dispatcher)
    {
        _infoText = infoText;
        _logText = logText;
        _dispatcher = dispatcher;
        Directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Marquee", "EngineSpike");
        System.IO.Directory.CreateDirectory(Directory);
        SpikeLogPath = Path.Combine(Directory, "spike.log");
        MpvLogPath = Path.Combine(Directory, "mpv.log");
        File.WriteAllText(SpikeLogPath, "");
    }

    public void Line(string text)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        Append(stamped);
        _dispatcher.TryEnqueue(() => _logText.Text += stamped + "\n");
    }

    public void Block(string title, IEnumerable<string> lines)
    {
        string body = $"── {title} ──\n" + string.Join("\n", lines);
        Append($"{DateTime.Now:HH:mm:ss.fff}  {body}");
        _dispatcher.TryEnqueue(() => _logText.Text += body + "\n");
    }

    public void Info(string title, IEnumerable<string> lines)
    {
        string body = $"── {title} ──\n" + string.Join("\n", lines);
        Append($"{DateTime.Now:HH:mm:ss.fff}  {body}");
        _dispatcher.TryEnqueue(() => _infoText.Text = body);
    }

    private void Append(string text)
    {
        lock (_fileLock)
        {
            File.AppendAllText(SpikeLogPath, text + Environment.NewLine);
        }
    }
}
