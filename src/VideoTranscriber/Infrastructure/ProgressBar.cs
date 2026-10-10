using System.Diagnostics;
using System.Globalization;

namespace VideoTranscriber.Infrastructure;

/// <summary>
/// A single-line console progress bar. Safe to update from any thread. Messages written through
/// <see cref="ConsoleUi"/> while a bar is active appear above it. When output is redirected to a file, it prints a
/// line every 10% instead of redrawing.
/// </summary>
public sealed class ProgressBar : IDisposable
{
    private const int MinRedrawIntervalMs = 100;

    private static ProgressBar? active;

    private readonly string label;
    private readonly bool interactive;
    private readonly Stopwatch sinceLastDraw = Stopwatch.StartNew();
    private double fraction;
    private string detail = string.Empty;
    private int lastPercent = -1;
    private int lastLoggedDecile = -1;
    private int lastLineLength;
    private bool finished;

    public ProgressBar(string label)
    {
        this.label = label;
        interactive = !Console.IsOutputRedirected;
        lock (ConsoleUi.SyncRoot)
        {
            active = this;
            Draw(force: true);
        }
    }

    /// <summary>Updates the bar. <paramref name="value"/> is clamped to 0..1.</summary>
    public void Report(double value, string? detailText = null)
    {
        lock (ConsoleUi.SyncRoot)
        {
            if (finished)
            {
                return;
            }

            fraction = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            if (detailText is not null)
            {
                detail = detailText;
            }

            Draw(force: false);
        }
    }

    /// <summary>Shows 100% (or the given final state) and moves to the next line.</summary>
    public void Complete(string? detailText = null)
    {
        lock (ConsoleUi.SyncRoot)
        {
            if (finished)
            {
                return;
            }

            fraction = 1;
            if (detailText is not null)
            {
                detail = detailText;
            }

            Draw(force: true);
            Finish();
        }
    }

    public void Dispose()
    {
        lock (ConsoleUi.SyncRoot)
        {
            if (!finished)
            {
                Finish();
            }
        }
    }

    internal static void ClearActiveLine()
    {
        if (active is { interactive: true, lastLineLength: > 0 } bar)
        {
            Console.Out.Write('\r' + new string(' ', bar.lastLineLength) + '\r');
            Console.Out.Flush();
        }
    }

    internal static void RedrawActive() => active?.Draw(force: true);

    private void Finish()
    {
        finished = true;
        if (interactive)
        {
            Console.Out.WriteLine();
        }

        if (ReferenceEquals(active, this))
        {
            active = null;
        }
    }

    private void Draw(bool force)
    {
        int percent = (int)Math.Floor(fraction * 100);

        if (!interactive)
        {
            int decile = percent / 10;
            if (decile > lastLoggedDecile)
            {
                lastLoggedDecile = decile;
                Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{label.TrimEnd()}: {percent}% {detail}").TrimEnd());
            }

            return;
        }

        if (!force && percent == lastPercent && sinceLastDraw.ElapsedMilliseconds < MinRedrawIntervalMs)
        {
            return;
        }

        lastPercent = percent;
        sinceLastDraw.Restart();

        int width = GetConsoleWidth();
        int barWidth = Math.Clamp(width - label.Length - detail.Length - 12, 10, 30);
        int filled = (int)Math.Round(fraction * barWidth);
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{label} [{new string('#', filled)}{new string('-', barWidth - filled)}] {percent,3}%  {detail}");

        if (line.Length > width - 1)
        {
            line = line[..Math.Max(0, width - 1)];
        }

        string padding = line.Length < lastLineLength ? new string(' ', lastLineLength - line.Length) : string.Empty;
        Console.Out.Write('\r' + line + padding);
        Console.Out.Flush();
        lastLineLength = line.Length;
    }

    private static int GetConsoleWidth()
    {
        try
        {
            int width = Console.WindowWidth;
            return width > 20 ? width : 80;
        }
        catch (IOException)
        {
            return 80;
        }
    }
}
