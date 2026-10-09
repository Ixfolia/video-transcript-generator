using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using VideoTranscriber.Audio;
using VideoTranscriber.Cli;
using VideoTranscriber.Ffmpeg;
using VideoTranscriber.Infrastructure;
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

    private const string TempFilePrefix = "videotranscriber-";

    private readonly HttpClient httpClient;

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
            DeleteStaleTempFiles();

            List<InputFile> inputs = ValidateInputs(options.InputFiles);
            if (inputs.Count == 0)
            {
                return ExitFailure;
            }

            var downloader = new FileDownloader(httpClient);
            await new FfmpegSetup(downloader).EnsureAvailableAsync(cancellationToken);

            var extractor = new AudioExtractor();
            List<(InputFile Input, MediaInfo Media)> probed = await ProbeInputsAsync(extractor, inputs, cancellationToken);
            if (probed.Count == 0)
            {
                return ExitFailure;
            }

            var modelManager = new ModelManager(AppPaths.ModelDirectory, downloader);
            string modelPath = await modelManager.EnsureModelAsync(options.Model, cancellationToken);

            using Transcriber transcriber = LoadTranscriber(options, modelPath);
            string language = ValidateLanguage(options.Language);

            int failures = options.InputFiles.Count - probed.Count;
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
                ConsoleUi.Warning($"Finished in {FormatElapsed(total.Elapsed)}; {failures} of {options.InputFiles.Count} files failed.");
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

    private static List<InputFile> ValidateInputs(IReadOnlyList<string> paths)
    {
        var inputs = new List<InputFile>();
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
                continue;
            }

            if (Directory.Exists(fullPath))
            {
                ConsoleUi.Error($"'{fullPath}' is a folder. Pass a video file instead.");
            }
            else if (!File.Exists(fullPath))
            {
                ConsoleUi.Error($"File not found: {fullPath}", "Check the spelling, and put the path in quotes if it contains spaces.");
            }
            else
            {
                inputs.Add(new InputFile(fullPath));
            }
        }

        return inputs;
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

    private static Transcriber LoadTranscriber(CliOptions options, string modelPath)
    {
        var stopwatch = Stopwatch.StartNew();
        string size = FileDownloader.FormatSize(new FileInfo(modelPath).Length);
        ConsoleUi.Step($"Loading the '{options.Model.Name}' model ({size})...");

        Transcriber transcriber = Transcriber.Load(modelPath, options.CpuOnly, options.Verbose ? ConsoleUi.Dim : null);

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
        string outputDirectory = ResolveOutputDirectory(options.OutputDirectory, input.FullPath);

        ConsoleUi.Info(string.Empty);
        ConsoleUi.Step($"{input.FileName}  ({FormatDuration(media.Duration)}{(media.AudioCodec is null ? string.Empty : ", " + media.AudioCodec + " audio")})");
        if (media.AudioStreamCount > 1)
        {
            ConsoleUi.Dim($"The file has {media.AudioStreamCount} audio tracks; using the first one.");
        }

        string wavPath = Path.Combine(Path.GetTempPath(), $"{TempFilePrefix}{Guid.NewGuid():N}.wav");
        try
        {
            using (var progress = new ProgressBar("Extracting audio"))
            {
                await extractor.ExtractAsync(input.FullPath, wavPath, media.Duration,
                    fraction => progress.Report(fraction, $"{FormatDuration(media.Duration * fraction)} / {FormatDuration(media.Duration)}"),
                    cancellationToken);
                progress.Complete(FileDownloader.FormatSize(new FileInfo(wavPath).Length) + " WAV");
            }

            var settings = new TranscriptionSettings(language, options.Translate, Math.Clamp(Environment.ProcessorCount, 1, 8));
            TranscriptionResult result;
            var stopwatch = Stopwatch.StartNew();
            using (var progress = new ProgressBar("Transcribing    "))
            {
                result = await transcriber.TranscribeAsync(
                    wavPath,
                    settings,
                    fraction => progress.Report(fraction, $"{FormatDuration(media.Duration * fraction)} / {FormatDuration(media.Duration)}"),
                    segment => ConsoleUi.Dim($"  [{TimestampFormatter.ToVtt(segment.Start)[..8]}] {segment.Text}"),
                    cancellationToken);
                progress.Complete($"{result.Segments.Count} segments in {FormatElapsed(stopwatch.Elapsed)}");
            }

            if (options.Language == TranscriptionSettings.AutoDetect && result.Language is not null)
            {
                ConsoleUi.Info($"Detected language: {result.Language}");
            }

            if (result.Segments.Count == 0)
            {
                ConsoleUi.Warning("No speech was recognized in this file. Empty transcript files will still be written.");
            }

            var transcript = new Transcript(
                SourceFile: input.FullPath,
                Model: options.Model.Name,
                Language: result.Language,
                Translated: options.Translate,
                Duration: media.Duration,
                Segments: result.Segments);

            IReadOnlyList<string> written = WriteOutputs(transcript, outputDirectory, options.Formats);
            ConsoleUi.Success("Saved:");
            foreach (string path in written)
            {
                ConsoleUi.Info("  " + path);
            }
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    private static IReadOnlyList<string> WriteOutputs(Transcript transcript, string outputDirectory, IReadOnlyList<OutputFormat> formats)
    {
        try
        {
            return new TranscriptOutputService().WriteAll(transcript, outputDirectory, formats);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UserFacingException(
                $"Couldn't write the transcript files to {outputDirectory}: {ex.Message}",
                "Use --output to choose a folder you can write to.",
                ex);
        }
    }

    private static string ResolveOutputDirectory(string? requested, string inputPath)
    {
        string directory = string.IsNullOrWhiteSpace(requested)
            ? Path.GetDirectoryName(inputPath)!
            : Path.GetFullPath(requested.Trim().Trim('"'));

        // Fail before spending minutes transcribing if the folder can't be written.
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $".videotranscriber-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new UserFacingException(
                $"Can't write to the output folder {directory}: {ex.Message}",
                "Use --output to choose a folder you can write to.",
                ex);
        }

        return directory;
    }

    /// <summary>
    /// Temp WAV files are deleted at the end of every run, but a crash or a force-closed window can leave one behind.
    /// Remove leftovers that are clearly not in use by another running instance.
    /// </summary>
    private static void DeleteStaleTempFiles()
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-2);
            foreach (string path in Directory.EnumerateFiles(Path.GetTempPath(), TempFilePrefix + "*.wav"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }

    private static string FormatDuration(TimeSpan time) =>
        time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    private static string FormatElapsed(TimeSpan time) =>
        time.TotalMinutes >= 1
            ? $"{(int)time.TotalMinutes}m {time.Seconds}s"
            : $"{time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record InputFile(string FullPath)
    {
        public string FileName => Path.GetFileName(FullPath);
    }
}
