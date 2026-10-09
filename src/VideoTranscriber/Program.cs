using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Text;
using VideoTranscriber;
using VideoTranscriber.Cli;
using VideoTranscriber.Infrastructure;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // Some hosts don't allow changing the encoding; the default still works for most text.
}

// Started by double-clicking or by dropping a file on the exe: keep the window open at the end so the output
// (and any error) can be read.
bool pauseBeforeExit = ConsoleUi.IsStandaloneConsoleWindow();

using HttpClient httpClient = FileDownloader.CreateHttpClient();
var app = new TranscriptionApp(httpClient);
RootCommand rootCommand = CommandLineDefinition.Create(app.RunAsync);

int exitCode;
if (args.Length == 0)
{
    ConsoleUi.Info("Drag a video file onto VideoTranscriber.exe, or run it from a terminal:");
    ConsoleUi.Info(string.Empty);
    await rootCommand.Parse(["--help"]).InvokeAsync();
    exitCode = TranscriptionApp.ExitFailure;
}
else
{
    ParseResult parseResult = rootCommand.Parse(args);
    CommandLineAction? versionAction = rootCommand.Options.OfType<VersionOption>().FirstOrDefault()?.Action;
    if (parseResult.Errors.Count > 0 && parseResult.Action is not HelpAction && !ReferenceEquals(parseResult.Action, versionAction))
    {
        // Show just the problem instead of System.CommandLine's default of repeating the whole help text.
        foreach (ParseError error in parseResult.Errors)
        {
            ConsoleUi.Error(error.Message);
        }

        ConsoleUi.Info("Run 'VideoTranscriber --help' to see all options.");
        exitCode = TranscriptionApp.ExitUsageError;
    }
    else
    {
        exitCode = await parseResult.InvokeAsync(new InvocationConfiguration
        {
            // Ctrl+C cancels the token passed to the app; give it time to stop FFmpeg/Whisper and delete temp files.
            ProcessTerminationTimeout = TimeSpan.FromSeconds(10),
        });
    }
}

if (pauseBeforeExit)
{
    ConsoleUi.PauseBeforeExit();
}

return exitCode;
