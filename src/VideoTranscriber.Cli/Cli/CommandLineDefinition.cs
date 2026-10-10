using System.CommandLine;
using System.Text.RegularExpressions;
using VideoTranscriber.Models;
using VideoTranscriber.Output;

namespace VideoTranscriber.Cli;

/// <summary>Defines the command-line interface using System.CommandLine.</summary>
public static partial class CommandLineDefinition
{
    public static RootCommand Create(Func<CliOptions, CancellationToken, Task<int>> run)
    {
        var videoArgument = new Argument<string[]>("video")
        {
            Description = "Video (or audio) file to transcribe. You can pass several files, or drag them onto VideoTranscriber.exe.",
            Arity = ArgumentArity.OneOrMore,
        };
        videoArgument.Validators.Add(result =>
        {
            // Without this, a mistyped option such as "--modle" would be taken as a file name.
            foreach (string token in result.Tokens.Select(t => t.Value))
            {
                if (token.StartsWith('-') && !File.Exists(token))
                {
                    result.AddError($"Unknown option '{token}'.");
                }
            }
        });

        var modelOption = new Option<string>("--model", "-m")
        {
            Description = "Whisper model size. Bigger is more accurate but slower: tiny, base, small, medium.",
            DefaultValueFactory = _ => "base",
            HelpName = "tiny|base|small|medium",
        };
        modelOption.AcceptOnlyFromAmong(WhisperModelInfo.Names.ToArray());

        var languageOption = new Option<string>("--language", "-l")
        {
            Description = "Spoken language as a code such as en, de, es, fr, ja, or 'auto' to detect it.",
            DefaultValueFactory = _ => "auto",
            HelpName = "code",
        };
        languageOption.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (value is null || !LanguagePattern().IsMatch(value))
            {
                result.AddError($"'{value}' is not a language code. Use a code such as en, de or ja, or 'auto'.");
            }
        });

        var formatOption = new Option<string>("--format", "-f")
        {
            Description = "Comma-separated output formats: txt, srt, vtt, json, or all.",
            DefaultValueFactory = _ => "txt,srt",
            HelpName = "txt,srt,vtt,json",
        };
        formatOption.Validators.Add(result =>
        {
            if (!OutputFormatParser.TryParse([result.GetValueOrDefault<string>() ?? string.Empty], out _, out string? error))
            {
                result.AddError(error!);
            }
        });

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Folder for the transcript files. Default: the folder containing the video.",
            HelpName = "folder",
        };

        var translateOption = new Option<bool>("--translate", "-t")
        {
            Description = "Translate the speech into English instead of transcribing it in the original language.",
        };

        var cpuOption = new Option<bool>("--cpu")
        {
            Description = "Use the CPU only, even if a supported GPU is available.",
        };

        var verboseOption = new Option<bool>("--verbose")
        {
            Description = "Show detailed diagnostic output from the speech recognition engine.",
        };

        var root = new RootCommand(
            "VideoTranscriber - create transcripts and subtitles from video files, offline, using OpenAI Whisper." +
            Environment.NewLine + Environment.NewLine +
            "Examples:" + Environment.NewLine +
            "  VideoTranscriber \"C:\\videos\\talk.mp4\"" + Environment.NewLine +
            "  VideoTranscriber talk.mp4 --model small --format srt,vtt" + Environment.NewLine +
            "  VideoTranscriber interview.mkv --language de --translate --output C:\\transcripts")
        {
            videoArgument,
            modelOption,
            languageOption,
            formatOption,
            outputOption,
            translateOption,
            cpuOption,
            verboseOption,
        };

        root.SetAction((parseResult, cancellationToken) =>
        {
            var options = new CliOptions(
                InputFiles: parseResult.GetRequiredValue(videoArgument),
                Model: WhisperModelInfo.Parse(parseResult.GetRequiredValue(modelOption)),
                Language: parseResult.GetRequiredValue(languageOption).ToLowerInvariant(),
                OutputDirectory: parseResult.GetValue(outputOption),
                Translate: parseResult.GetValue(translateOption),
                Formats: ParseFormats(parseResult.GetRequiredValue(formatOption)),
                CpuOnly: parseResult.GetValue(cpuOption),
                Verbose: parseResult.GetValue(verboseOption));

            return run(options, cancellationToken);
        });

        return root;
    }

    private static IReadOnlyList<OutputFormat> ParseFormats(string value) =>
        OutputFormatParser.TryParse([value], out IReadOnlyList<OutputFormat> formats, out string? error)
            ? formats
            : throw new InvalidOperationException(error); // Unreachable: the option validator already rejected it.

    [GeneratedRegex("^(auto|[a-zA-Z]{2,3})$")]
    private static partial Regex LanguagePattern();
}
