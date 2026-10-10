using VideoTranscriber.Audio;
using VideoTranscriber.Infrastructure;
using VideoTranscriber.Output;
using VideoTranscriber.Transcription;

namespace VideoTranscriber.Jobs;

/// <summary>Settings shared by every file in a run.</summary>
public sealed record TranscriptionJobOptions(
    string ModelName,
    string Language,
    bool Translate,
    IReadOnlyList<OutputFormat> Formats);

public enum JobStage
{
    ExtractingAudio,
    Transcribing,
    Saving,
}

/// <summary>Receives progress for one file. Methods may be called from background threads.</summary>
public interface ITranscriptionJobObserver
{
    void StageStarted(JobStage stage);

    /// <param name="fraction">Progress of the current stage, 0..1.</param>
    void StageProgress(JobStage stage, double fraction);

    void StageCompleted(JobStage stage);

    void SegmentRecognized(TranscriptSegment segment);
}

public sealed record TranscriptionJobResult(
    IReadOnlyList<string> WrittenFiles,
    string? Language,
    IReadOnlyList<TranscriptSegment> Segments,
    long WavBytes);

/// <summary>
/// Transcribes one file end to end: extract the audio to a temporary WAV, run Whisper over it, write the requested
/// formats, and always delete the temporary file. Used by both the command-line and the desktop app.
/// </summary>
public sealed class TranscriptionJobRunner
{
    public const string TempFilePrefix = "videotranscriber-";

    private readonly AudioExtractor extractor;
    private readonly Transcriber transcriber;

    public TranscriptionJobRunner(AudioExtractor extractor, Transcriber transcriber)
    {
        this.extractor = extractor;
        this.transcriber = transcriber;
    }

    public async Task<TranscriptionJobResult> RunAsync(
        string inputPath,
        MediaInfo media,
        string outputDirectory,
        string outputBaseName,
        TranscriptionJobOptions options,
        ITranscriptionJobObserver observer,
        CancellationToken cancellationToken)
    {
        EnsureWritable(outputDirectory);

        string wavPath = Path.Combine(Path.GetTempPath(), $"{TempFilePrefix}{Guid.NewGuid():N}.wav");
        try
        {
            observer.StageStarted(JobStage.ExtractingAudio);
            await extractor.ExtractAsync(inputPath, wavPath, media.Duration,
                fraction => observer.StageProgress(JobStage.ExtractingAudio, fraction), cancellationToken);
            long wavBytes = new FileInfo(wavPath).Length;
            observer.StageCompleted(JobStage.ExtractingAudio);

            observer.StageStarted(JobStage.Transcribing);
            var settings = new TranscriptionSettings(options.Language, options.Translate, Math.Clamp(Environment.ProcessorCount, 1, 8));
            TranscriptionResult result = await transcriber.TranscribeAsync(
                wavPath,
                settings,
                fraction => observer.StageProgress(JobStage.Transcribing, fraction),
                observer.SegmentRecognized,
                cancellationToken);
            observer.StageCompleted(JobStage.Transcribing);

            observer.StageStarted(JobStage.Saving);
            var transcript = new Transcript(
                SourceFile: inputPath,
                Model: options.ModelName,
                Language: result.Language,
                Translated: options.Translate,
                Duration: media.Duration,
                Segments: result.Segments);
            IReadOnlyList<string> written = WriteOutputs(transcript, outputDirectory, outputBaseName, options.Formats);
            observer.StageCompleted(JobStage.Saving);

            return new TranscriptionJobResult(written, result.Language, result.Segments, wavBytes);
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    /// <summary>Fails early, before minutes of transcription, if the output folder can't be written.</summary>
    public static void EnsureWritable(string directory)
    {
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
                "Choose a different output folder that you can write to.",
                ex);
        }
    }

    /// <summary>
    /// Temp WAV files are deleted at the end of every run, but a crash or a force-closed window can leave one behind.
    /// Remove leftovers that are clearly not in use by another running instance.
    /// </summary>
    public static void DeleteStaleTempFiles()
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

    private static IReadOnlyList<string> WriteOutputs(Transcript transcript, string outputDirectory, string baseName, IReadOnlyList<OutputFormat> formats)
    {
        try
        {
            return new TranscriptOutputService().WriteAll(transcript, outputDirectory, baseName, formats);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UserFacingException(
                $"Couldn't write the transcript files to {outputDirectory}: {ex.Message}",
                "Choose a different output folder that you can write to.",
                ex);
        }
    }

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
}
