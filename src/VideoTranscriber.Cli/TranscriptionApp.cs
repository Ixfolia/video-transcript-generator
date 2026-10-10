using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using VideoTranscriber.Audio;
using VideoTranscriber.Cli;
using VideoTranscriber.Ffmpeg;
using VideoTranscriber.Infrastructure;
using VideoTranscriber.Jobs;
using VideoTranscriber.Models;
using VideoTranscriber.Output;
using VideoTranscriber.Transcription;

namespace VideoTranscriber;

/// <summary>Runs the whole pipeline: check inputs, set up FFmpeg and the model, then extract, transcribe and save.</summary>
public sealed class TranscriptionApp
{
    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsageError = 2;
    public const int ExitCancelled = 130;

    private readonly HttpClient httpClient;
    private readonly ConsoleStatusReporter status = new();

    public TranscriptionApp(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public static string Version =>
        typeof(TranscriptionApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    public async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        try
        {
            ConsoleUi.Info($"VideoTranscriber {Version}");
            TranscriptionJobRunner.DeleteStaleTempFiles();

            (List<InputFile> inputs, int rejected) = ValidateInputs(options.InputFiles);
            int fileCount = inputs.Count + rejected;
            if (inputs.Count == 0)
            {
                return ExitFailure;
            }

            var downloader = new FileDownloader(httpClient, status);
            await new FfmpegSetup(downloader, status).EnsureAvailableAsync(cancellationToken);

            var extractor = new AudioExtractor();
            List<(InputFile Input, MediaInfo Media)> probed = await ProbeInputsAsync(extractor, inputs, cancellationToken);
            if (probed.Count == 0)
            {
                return ExitFailure;
            }

            probed = AssignOutputNames(probed, options.OutputDirectory);

            var modelManager = new ModelManager(AppPaths.ModelDirectory, downloader, status);
            string modelPath = await modelManager.EnsureModelAsync(options.Model, cancellationToken);

            using Transcriber transcriber = LoadTranscriber(options, modelPath, status);
            string language = ValidateLanguage(options.Language);

            int failures = fileCount - probed.Count;
            foreach ((InputFile input, MediaInfo media) in probed)
            {
                try
                {
                    await TranscribeFileAsync(input, media, options, language, extractor, transcriber, cancellationToken);
                }
                catch (UserFacingException ex) when (probed.Count > 1)
                {
                    // With several files, report the problem and carry on with the rest.
                    ConsoleUi.Error(ex.Message, ex.Hint);
                    failures++;
                }
            }

            ConsoleUi.Info(string.Empty);
            if (failures > 0)
            {
                ConsoleUi.Warning($"Finished in {FormatElapsed(total.Elapsed)}; {failures} of {fileCount} files failed.");
                return ExitFailure;
            }

            ConsoleUi.Success($"Done in {FormatElapsed(total.Elapsed)}.");
            return ExitSuccess;
        }
        catch (UserFacingException ex)
        {
            ConsoleUi.Error(ex.Message, ex.Hint);
            if (options.Verbose && ex.InnerException is not null)
            {
                ConsoleUi.Dim(ex.InnerException.ToString());
            }

            return ExitFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ConsoleUi.Warning("Cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            ConsoleUi.Error($"Unexpected error: {ex.Message}", "Please report this at the project's GitHub issues page, including the details below.");
            ConsoleUi.Dim(ex.ToString());
            return ExitFailure;
        }
    }

    /// <summary>Returns the usable, de-duplicated input files and how many paths were rejected.</summary>
    private static (List<InputFile> Inputs, int Rejected) ValidateInputs(IReadOnlyList<string> paths)
    {
        var inputs = new List<InputFile>();
        int rejected = 0;
        var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (string rawPath in paths)
        {
            string path = rawPath.Trim().Trim('"');
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ConsoleUi.Error($"'{rawPath}' is not a valid file path.");
                rejected++;
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                ConsoleUi.Error($"'{fullPath}' is a folder. Pass a video file instead.");
                rejected++;
            }
            else if (!File.Exists(fullPath))
            {
                ConsoleUi.Error($"File not found: {fullPath}", "Check the spelling, and put the path in quotes if it contains spaces.");
                rejected++;
            }
            else if (seen.Add(fullPath))
            {
                inputs.Add(new InputFile(fullPath));
            }
            else
            {
                ConsoleUi.Dim($"Skipping {Path.GetFileName(fullPath)}: it was given more than once.");
            }
        }

        return (inputs, rejected);
    }

    private static async Task<List<(InputFile, MediaInfo)>> ProbeInputsAsync(AudioExtractor extractor, List<InputFile> inputs, CancellationToken cancellationToken)
    {
        var probed = new List<(InputFile, MediaInfo)>();
        foreach (InputFile input in inputs)
        {
            try
            {
                probed.Add((input, await extractor.ProbeAsync(input.FullPath, cancellationToken)));
            }
            catch (UserFacingException ex)
            {
                ConsoleUi.Error(ex.Message, ex.Hint);
            }
        }

        return probed;
    }

    private static Transcriber LoadTranscriber(CliOptions options, string modelPath, IStatusReporter status)
    {
        var stopwatch = Stopwatch.StartNew();
        string size = FileDownloader.FormatSize(new FileInfo(modelPath).Length);
        ConsoleUi.Step($"Loading the '{options.Model.Name}' model ({size})...");

        Transcriber transcriber = Transcriber.Load(modelPath, options.CpuOnly, status, options.Verbose ? ConsoleUi.Dim : null);

        ConsoleUi.Info($"Model loaded in {stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s, running on {transcriber.RuntimeDescription}.");
        return transcriber;
    }

    private static string ValidateLanguage(string language)
    {
        if (language == TranscriptionSettings.AutoDetect)
        {
            return language;
        }

        IReadOnlySet<string> supported = Transcriber.SupportedLanguages();
        if (supported.Count > 0 && !supported.Contains(language))
        {
            throw new UserFacingException(
                $"Whisper doesn't support the language code '{language}'.",
                "Supported codes: " + string.Join(", ", supported.Order(StringComparer.Ordinal)));
        }

        return language;
    }

    private static async Task TranscribeFileAsync(
        InputFile input,
        MediaInfo media,
        CliOptions options,
        string language,
        AudioExtractor extractor,
        Transcriber transcriber,
        CancellationToken cancellationToken)
    {
        ConsoleUi.Info(string.Empty);
        ConsoleUi.Step($"{input.FileName}  ({ConsoleJobObserver.FormatTime(media.Duration)}{(media.AudioCodec is null ? string.Empty : ", " + media.AudioCodec + " audio")})");
        if (media.AudioStreamCount > 1)
        {
            ConsoleUi.Dim($"The file has {media.AudioStreamCount} audio tracks; using the first one.");
        }

        if (input.OutputBaseName != Path.GetFileNameWithoutExtension(input.FullPath))
        {
            ConsoleUi.Dim($"Another input has the same name, so this one is saved as '{input.OutputBaseName}.*'.");
        }

        var jobOptions = new TranscriptionJobOptions(options.Model.Name, language, options.Translate, options.Formats);
        var stopwatch = Stopwatch.StartNew();
        TranscriptionJobResult result;
        using (var observer = new ConsoleJobObserver(media.Duration))
        {
            result = await new TranscriptionJobRunner(extractor, transcriber).RunAsync(
                input.FullPath, media, input.OutputDirectory, input.OutputBaseName, jobOptions, observer, cancellationToken);
        }

        ConsoleUi.Info($"{result.Segments.Count} segments in {FormatElapsed(stopwatch.Elapsed)}.");
        if (options.Language == TranscriptionSettings.AutoDetect && result.Language is not null)
        {
            ConsoleUi.Info($"Detected language: {result.Language}");
        }

        if (result.Segments.Count == 0)
        {
            ConsoleUi.Warning("No speech was recognized in this file; the transcript files are empty.");
        }

        ConsoleUi.Success("Saved:");
        foreach (string path in result.WrittenFiles)
        {
            ConsoleUi.Info("  " + path);
        }
    }

    private static List<(InputFile, MediaInfo)> AssignOutputNames(List<(InputFile Input, MediaInfo Media)> files, string? requestedOutput)
    {
        string[] directories = files.Select(f => GetOutputDirectory(requestedOutput, f.Input.FullPath)).ToArray();
        IReadOnlyList<string> baseNames = OutputNaming.AssignBaseNames(
            files.Select((f, i) => (f.Input.FullPath, directories[i])).ToList());

        return files
            .Select((f, i) => (f.Input with { OutputDirectory = directories[i], OutputBaseName = baseNames[i] }, f.Media))
            .ToList();
    }

    private static string GetOutputDirectory(string? requested, string inputPath)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetDirectoryName(inputPath)!;
        }

        try
        {
            return Path.GetFullPath(requested.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UserFacingException($"'{requested}' is not a valid output folder.", innerException: ex);
        }
    }

    private static string FormatElapsed(TimeSpan time) =>
        time.TotalMinutes >= 1
            ? $"{(int)time.TotalMinutes}m {time.Seconds}s"
            : $"{time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";

    private sealed record InputFile(string FullPath)
    {
        public string FileName => Path.GetFileName(FullPath);

        public string OutputDirectory { get; init; } = Path.GetDirectoryName(FullPath)!;

        public string OutputBaseName { get; init; } = Path.GetFileNameWithoutExtension(FullPath);
    }
}
