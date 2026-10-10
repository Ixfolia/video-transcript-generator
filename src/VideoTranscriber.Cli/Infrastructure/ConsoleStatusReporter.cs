using VideoTranscriber.Jobs;
using VideoTranscriber.Output;

namespace VideoTranscriber.Infrastructure;

/// <summary>Shows core status messages and progress in the console.</summary>
public sealed class ConsoleStatusReporter : IStatusReporter
{
    public void Info(string message) => ConsoleUi.Info(message);

    public void Step(string message) => ConsoleUi.Step(message);

    public void Success(string message) => ConsoleUi.Success(message);

    public void Warning(string message) => ConsoleUi.Warning(message);

    public void Detail(string message) => ConsoleUi.Dim(message);

    public IProgressReporter BeginProgress(string label) => new ProgressBar(label);

    public Task<bool> ConfirmAsync(string question, bool defaultAnswer) =>
        Task.FromResult(ConsoleUi.Confirm(question, defaultAnswer));
}

/// <summary>Shows one file's progress as console progress bars, with recognized text printed as it arrives.</summary>
public sealed class ConsoleJobObserver : ITranscriptionJobObserver, IDisposable
{
    private readonly TimeSpan duration;
    private ProgressBar? bar;

    public ConsoleJobObserver(TimeSpan duration)
    {
        this.duration = duration;
    }

    public void StageStarted(JobStage stage)
    {
        bar?.Dispose();
        bar = stage switch
        {
            JobStage.ExtractingAudio => new ProgressBar("Extracting audio"),
            JobStage.Transcribing => new ProgressBar("Transcribing    "),
            _ => null,
        };
    }

    public void StageProgress(JobStage stage, double fraction) =>
        bar?.Report(fraction, $"{FormatTime(duration * Math.Clamp(fraction, 0, 1))} / {FormatTime(duration)}");

    public void StageCompleted(JobStage stage)
    {
        bar?.Complete();
        bar = null;
    }

    public void SegmentRecognized(TranscriptSegment segment) =>
        ConsoleUi.Dim($"  [{TimestampFormatter.ToVtt(segment.Start)[..8]}] {segment.Text}");

    public void Dispose() => bar?.Dispose();

    public static string FormatTime(TimeSpan time) =>
        time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
}
