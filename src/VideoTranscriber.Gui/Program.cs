using Avalonia;

namespace VideoTranscriber.Gui;

internal static class Program
{
    /// <summary>
    /// Files passed on the command line (which is what Windows does when videos are dropped onto the exe) are
    /// added to the queue. "--smoke-test &lt;video&gt; &lt;output-folder&gt;" transcribes one file with the tiny model and
    /// exits with 0 on success; the release build uses it to check the real window on Windows.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        App.StartupArguments = StartupArguments.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

/// <summary>What the app was asked to do on startup.</summary>
internal sealed record StartupArguments(IReadOnlyList<string> Files, string? SmokeTestVideo, string? SmokeTestOutput)
{
    public bool IsSmokeTest => SmokeTestVideo is not null;

    public static StartupArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count >= 3 && args[0] == "--smoke-test")
        {
            return new StartupArguments([], args[1], args[2]);
        }

        return new StartupArguments(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList(), null, null);
    }
}
