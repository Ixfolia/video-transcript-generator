using System.ComponentModel;
using System.Text.RegularExpressions;
using FFMpegCore;
using FFMpegCore.Exceptions;
using VideoTranscriber.Infrastructure;

namespace VideoTranscriber.Audio;

/// <summary>Basic facts about the input file.</summary>
public sealed record MediaInfo(TimeSpan Duration, int AudioStreamCount, string? AudioCodec, bool HasVideo);

/// <summary>Uses FFmpeg to inspect a video and extract its audio as 16 kHz mono 16-bit WAV (what Whisper expects).</summary>
public sealed partial class AudioExtractor
{
    public const int WhisperSampleRate = 16_000;

    /// <summary>Reads the container metadata. Fails with a clear message for corrupt files or files without audio.</summary>
    public async Task<MediaInfo> ProbeAsync(string inputPath, CancellationToken cancellationToken)
    {
        IMediaAnalysis analysis;
        try
        {
            analysis = await FFProbe.AnalyseAsync(inputPath, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is FFProbeException or FFMpegException && !cancellationToken.IsCancellationRequested)
        {
            throw new UserFacingException(
                $"Couldn't read '{Path.GetFileName(inputPath)}'. The file may be corrupt, incomplete, or not a video/audio format FFmpeg understands.",
                Summarize(ex.Message),
                ex);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            throw new UserFacingException($"Couldn't run ffprobe: {ex.Message}", "Make sure ffprobe works by running 'ffprobe -version' in a terminal.", ex);
        }

        if (analysis.AudioStreams.Count == 0)
        {
            throw new UserFacingException(
                $"'{Path.GetFileName(inputPath)}' has no audio track, so there is nothing to transcribe.");
        }

        return new MediaInfo(
            analysis.Duration,
            analysis.AudioStreams.Count,
            analysis.PrimaryAudioStream?.CodecName,
            analysis.VideoStreams.Count > 0);
    }

    /// <summary>
    /// Extracts the first audio track to <paramref name="wavPath"/>. FFmpeg streams the file, so memory use stays
    /// flat regardless of video length.
    /// </summary>
    public async Task ExtractAsync(string inputPath, string wavPath, TimeSpan duration, Action<double>? onProgress, CancellationToken cancellationToken)
    {
        FFMpegArgumentProcessor processor = FFMpegArguments
            .FromFileInput(inputPath)
            .OutputToFile(wavPath, overwrite: true, options => options
                .WithCustomArgument($"-map 0:a:0 -vn -sn -dn -ac 1 -ar {WhisperSampleRate} -c:a pcm_s16le")
                .ForceFormat("wav"))
            .CancellableThrough(cancellationToken);

        if (onProgress is not null && duration > TimeSpan.Zero)
        {
            processor = processor.NotifyOnProgress(percent => onProgress(percent / 100.0), duration);
        }

        try
        {
            await processor.ProcessAsynchronously(throwOnError: true);
        }
        catch (FFMpegException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UserFacingException(
                $"FFmpeg couldn't extract the audio from '{Path.GetFileName(inputPath)}'. The file may be corrupt or use an unsupported codec.",
                Summarize(ex.FFMpegErrorOutput is { Length: > 0 } output ? output : ex.Message),
                ex);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var info = new FileInfo(wavPath);
        if (!info.Exists || info.Length <= 44)
        {
            throw new UserFacingException(
                $"'{Path.GetFileName(inputPath)}' contains an audio track, but no audio could be decoded from it.");
        }
    }

    /// <summary>Keeps the last few meaningful lines of FFmpeg output (the actual error is usually at the end).</summary>
    private static string Summarize(string ffmpegOutput)
    {
        string[] lines = ffmpegOutput
            .Replace("ffprobe exited with non-zero exit-code", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("ffmpeg version", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("ffprobe version", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("built with", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("configuration:", StringComparison.OrdinalIgnoreCase)
                        && !l.StartsWith("lib", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim('(', ')', ' '))
            .Select(l => StreamPrefix().Replace(l, string.Empty))
            .Select(l => l.Contains(": ", StringComparison.Ordinal) && Path.IsPathRooted(l.Split(": ")[0]) ? l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..] : l)
            .Where(l => l.Length > 0)
            .TakeLast(3)
            .ToArray();

        return lines.Length == 0 ? string.Empty : "FFmpeg said: " + string.Join(Environment.NewLine + "  ", lines);
    }

    // Exit code prefix and "[mov,mp4,... @ 0x55d0c1a2b3c0] " decoder tags at the start of FFmpeg log lines.
    [GeneratedRegex(@"^(\d+ - )?\[[^\]]+ @ 0x[0-9a-fA-F]+\]\s*")]
    private static partial Regex StreamPrefix();
}
