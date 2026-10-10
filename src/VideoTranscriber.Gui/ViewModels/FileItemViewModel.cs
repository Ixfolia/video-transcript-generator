using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoTranscriber.Audio;
using VideoTranscriber.Jobs;
using VideoTranscriber.Output;
using VideoTranscriber.Transcription;

namespace VideoTranscriber.Gui.ViewModels;

public enum FileStatus
{
    Waiting,
    Preparing,
    ExtractingAudio,
    Transcribing,
    Saving,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One video in the queue.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly MainWindowViewModel owner;
    private readonly StringBuilder transcript = new();

    public FileItemViewModel(string fullPath, MainWindowViewModel owner)
    {
        FullPath = fullPath;
        this.owner = owner;
    }

    public string FullPath { get; }

    public string FileName => Path.GetFileName(FullPath);

    public string Folder => Path.GetDirectoryName(FullPath) ?? string.Empty;

    public MediaInfo? Media { get; set; }

    public IReadOnlyList<string> OutputFiles { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsActive), nameof(IsDone), nameof(IsFailed), nameof(IsCancelled),
        nameof(ShowProgress), nameof(CanRemove), nameof(IsIndeterminate))]
    private FileStatus status;

    /// <summary>0..100 within the current stage.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double progress;

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string? errorHint;

    [ObservableProperty]
    private string durationText = string.Empty;

    [ObservableProperty]
    private string? resultText;

    [ObservableProperty]
    private string transcriptText = string.Empty;

    public bool IsActive => Status is FileStatus.Preparing or FileStatus.ExtractingAudio or FileStatus.Transcribing or FileStatus.Saving;

    public bool IsDone => Status == FileStatus.Done;

    public bool IsFailed => Status == FileStatus.Failed;

    public bool IsCancelled => Status == FileStatus.Cancelled;

    public bool ShowProgress => IsActive;

    public bool IsIndeterminate => Status is FileStatus.Preparing or FileStatus.Saving;

    public bool CanRemove => !IsActive;

    public bool HasTranscript => transcript.Length > 0;

    public string StatusText => Status switch
    {
        FileStatus.Waiting => "Waiting",
        FileStatus.Preparing => "Preparing…",
        FileStatus.ExtractingAudio => string.Create(CultureInfo.InvariantCulture, $"Extracting audio… {Progress:0}%"),
        FileStatus.Transcribing => string.Create(CultureInfo.InvariantCulture, $"Transcribing… {Progress:0}%"),
        FileStatus.Saving => "Saving…",
        FileStatus.Done => "Done",
        FileStatus.Failed => "Failed",
        FileStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };

    /// <summary>The transcript file to open when the user clicks "Open": the plain text if there is one.</summary>
    public string? PrimaryOutput =>
        OutputFiles.FirstOrDefault(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) ?? OutputFiles.FirstOrDefault();

    [RelayCommand]
    private void Open() => owner.OpenOutput(this);

    [RelayCommand]
    private void ShowInFolder() => owner.ShowInFolder(this);

    [RelayCommand]
    private void Remove() => owner.RemoveFile(this);

    [RelayCommand]
    private void Retry() => owner.Retry(this);

    public void SetMedia(MediaInfo media)
    {
        Media = media;
        DurationText = FormatTime(media.Duration);
    }

    public void Reset()
    {
        Status = FileStatus.Waiting;
        Progress = 0;
        ErrorMessage = null;
        ErrorHint = null;
        ResultText = null;
        OutputFiles = [];
        transcript.Clear();
        TranscriptText = string.Empty;
        OnPropertyChanged(nameof(HasTranscript));
    }

    public void StartStage(JobStage stage)
    {
        Progress = 0;
        Status = stage switch
        {
            JobStage.ExtractingAudio => FileStatus.ExtractingAudio,
            JobStage.Transcribing => FileStatus.Transcribing,
            _ => FileStatus.Saving,
        };
    }

    public void AppendSegment(TranscriptSegment segment)
    {
        if (transcript.Length > 0)
        {
            transcript.Append('\n');
        }

        transcript.Append('[').Append(TimestampFormatter.ToVtt(segment.Start)[..8]).Append("]  ").Append(segment.Text);
        TranscriptText = transcript.ToString();
        OnPropertyChanged(nameof(HasTranscript));
    }

    public void Complete(TranscriptionJobResult result)
    {
        OutputFiles = result.WrittenFiles;
        OnPropertyChanged(nameof(PrimaryOutput));
        string language = result.Language is null ? string.Empty : $" · {WhisperLanguages.DisplayName(result.Language)}";
        ResultText = result.Segments.Count == 0
            ? "No speech was recognized; the transcript files are empty."
            : $"{result.Segments.Count} segments{language} · saved {string.Join(", ", result.WrittenFiles.Select(Path.GetExtension))}";
        Status = FileStatus.Done;
    }

    public void Fail(string message, string? hint)
    {
        ErrorMessage = message;
        ErrorHint = hint;
        Status = FileStatus.Failed;
    }

    public static string FormatTime(TimeSpan time) =>
        time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
