using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoTranscriber.Audio;
using VideoTranscriber.Ffmpeg;
using VideoTranscriber.Gui.Services;
using VideoTranscriber.Infrastructure;
using VideoTranscriber.Jobs;
using VideoTranscriber.Models;
using VideoTranscriber.Output;
using VideoTranscriber.Transcription;

namespace VideoTranscriber.Gui.ViewModels;

/// <summary>A choice in the model drop-down.</summary>
public sealed record ModelOption(WhisperModelInfo Model, string Title, string Description);

/// <summary>A choice in the language drop-down; a null code means auto-detect.</summary>
public sealed record LanguageOption(string? Code, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDialogService dialogs;
    private readonly SettingsStore settingsStore;
    private readonly IUiDispatcher dispatcher;
    private readonly HttpClient httpClient = FileDownloader.CreateHttpClient();

    private CancellationTokenSource? cancellation;
    private Transcriber? transcriber;
    private (string ModelPath, bool CpuOnly)? loadedTranscriber;

    public MainWindowViewModel(IDialogService dialogs, SettingsStore settingsStore, IUiDispatcher dispatcher)
    {
        this.dialogs = dialogs;
        this.settingsStore = settingsStore;
        this.dispatcher = dispatcher;

        Files.CollectionChanged += OnFilesChanged;
        ApplySettings(settingsStore.Load());
    }

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public IReadOnlyList<ModelOption> Models { get; } =
    [
        new(WhisperModelInfo.Parse("tiny"), "Tiny", "75 MB · fastest, basic accuracy"),
        new(WhisperModelInfo.Parse("base"), "Base", "142 MB · fast, good accuracy (recommended)"),
        new(WhisperModelInfo.Parse("small"), "Small", "466 MB · slower, better with accents and other languages"),
        new(WhisperModelInfo.Parse("medium"), "Medium", "1.5 GB · slowest, most accurate; a GPU is recommended"),
    ];

    public IReadOnlyList<LanguageOption> Languages { get; } =
        new[] { new LanguageOption(null, "Detect automatically") }
            .Concat(WhisperLanguages.All.Select(l => new LanguageOption(l.Code, l.Name)))
            .ToList();

    [ObservableProperty]
    private ModelOption selectedModel = null!;

    [ObservableProperty]
    private LanguageOption selectedLanguage = null!;

    [ObservableProperty]
    private bool translate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool formatTxt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool formatSrt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool formatVtt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool formatJson;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveToCustomFolder))]
    private bool saveNextToVideo = true;

    [ObservableProperty]
    private string? outputFolder;

    [ObservableProperty]
    private bool useGpu = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanEditSettings), nameof(CanClearFinished))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    private string statusMessage = "Add videos to get started.";

    [ObservableProperty]
    private bool statusIsError;

    [ObservableProperty]
    private string? activityLabel;

    [ObservableProperty]
    private double activityProgress;

    [ObservableProperty]
    private string? activityDetail;

    [ObservableProperty]
    private string? engineInfo;

    [ObservableProperty]
    private bool isDragOver;

    [ObservableProperty]
    private FileItemViewModel? selectedFile;

    public bool SaveToCustomFolder
    {
        get => !SaveNextToVideo;
        set => SaveNextToVideo = !value;
    }

    public bool HasFiles => Files.Count > 0;

    public bool CanEditSettings => !IsBusy;

    public bool CanClearFinished => !IsBusy && Files.Any(f => f.IsDone);

    public bool CanStart => !IsBusy && SelectedFormats().Count > 0 && Files.Any(f => f.Status != FileStatus.Done);

    public bool ShowActivity => ActivityLabel is not null;

    partial void OnActivityLabelChanged(string? value) => OnPropertyChanged(nameof(ShowActivity));

    // ---------------------------------------------------------------- Queue

    public void AddFiles(IEnumerable<string> paths)
    {
        var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        int added = 0;
        foreach (string raw in paths)
        {
            string path;
            try
            {
                path = Path.GetFullPath(raw.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                // A dropped folder: take the media files directly inside it.
                AddFiles(Directory.EnumerateFiles(path).Where(IsLikelyMediaFile).Order(StringComparer.OrdinalIgnoreCase));
                continue;
            }

            if (!File.Exists(path) || Files.Any(f => comparer.Equals(f.FullPath, path)))
            {
                continue;
            }

            var item = new FileItemViewModel(path, this);
            item.PropertyChanged += OnFileChanged;
            Files.Add(item);
            added++;
        }

        if (added > 0 && !IsBusy)
        {
            SetStatus($"{Files.Count(f => f.Status != FileStatus.Done)} file(s) ready. Press Start to transcribe.");
        }
    }

    [RelayCommand]
    private async Task AddVideosAsync() => AddFiles(await dialogs.PickVideosAsync());

    [RelayCommand]
    private async Task BrowseOutputFolderAsync()
    {
        string? folder = await dialogs.PickFolderAsync(OutputFolder);
        if (folder is not null)
        {
            OutputFolder = folder;
            SaveNextToVideo = false;
        }
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (FileItemViewModel item in Files.Where(f => f.IsDone).ToList())
        {
            RemoveFile(item);
        }
    }

    [RelayCommand]
    private async Task CopyTranscriptAsync()
    {
        if (SelectedFile is { HasTranscript: true } item)
        {
            await dialogs.CopyToClipboardAsync(item.TranscriptText);
            SetStatus($"Copied the transcript of {item.FileName}.");
        }
    }

    internal void RemoveFile(FileItemViewModel item)
    {
        if (item.CanRemove)
        {
            item.PropertyChanged -= OnFileChanged;
            Files.Remove(item);
        }
    }

    internal void Retry(FileItemViewModel item)
    {
        if (!IsBusy)
        {
            item.Reset();
        }
    }

    internal void OpenOutput(FileItemViewModel item)
    {
        if (item.PrimaryOutput is { } path)
        {
            dialogs.OpenFile(path);
        }
    }

    internal void ShowInFolder(FileItemViewModel item) => dialogs.ShowInFolder(item.PrimaryOutput ?? item.FullPath);

    // ---------------------------------------------------------------- Running

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunQueueAsync();

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        cancellation?.Cancel();
        SetStatus("Cancelling…");
    }

    /// <summary>Transcribes every file in the queue that isn't done yet. Returns true if all of them succeeded.</summary>
    public async Task<bool> RunQueueAsync()
    {
        IReadOnlyList<OutputFormat> formats = SelectedFormats();
        List<FileItemViewModel> queue = Files.Where(f => f.Status != FileStatus.Done).ToList();
        if (queue.Count == 0 || formats.Count == 0 || IsBusy)
        {
            return false;
        }

        SaveSettings();
        foreach (FileItemViewModel item in queue)
        {
            item.Reset();
        }

        IsBusy = true;
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        var status = new GuiStatusReporter(this, dispatcher, dialogs);
        int succeeded = 0;

        try
        {
            TranscriptionJobRunner.DeleteStaleTempFiles();
            var downloader = new FileDownloader(httpClient, status);

            SetStatus("Checking FFmpeg…");
            await new FfmpegSetup(downloader, status).EnsureAvailableAsync(token);

            var extractor = new AudioExtractor();
            var ready = new List<FileItemViewModel>();
            foreach (FileItemViewModel item in queue)
            {
                token.ThrowIfCancellationRequested();
                if (!Files.Contains(item))
                {
                    continue; // removed from the list while waiting
                }

                item.Status = FileStatus.Preparing;
                try
                {
                    item.SetMedia(await extractor.ProbeAsync(item.FullPath, token));
                    ready.Add(item);
                }
                catch (UserFacingException ex)
                {
                    item.Fail(ex.Message, ex.Hint);
                    AddLog($"{item.FileName}: {ex.Message}");
                }
            }

            if (ready.Count == 0)
            {
                SetStatus("None of the files could be read. See the messages next to each file.", isError: true);
                return false;
            }

            WhisperModelInfo model = SelectedModel.Model;
            string modelPath = await new ModelManager(AppPaths.ModelDirectory, downloader, status).EnsureModelAsync(model, token);
            await LoadTranscriberAsync(model, modelPath, cpuOnly: !UseGpu, status);

            var options = new TranscriptionJobOptions(model.Name, SelectedLanguage.Code ?? TranscriptionSettings.AutoDetect, Translate, formats);
            string[] directories = ready.Select(f => SaveNextToVideo || string.IsNullOrWhiteSpace(OutputFolder) ? f.Folder : OutputFolder!).ToArray();
            IReadOnlyList<string> baseNames = OutputNaming.AssignBaseNames(ready.Select((f, i) => (f.FullPath, directories[i])).ToList());
            var runner = new TranscriptionJobRunner(extractor, transcriber!);

            for (int i = 0; i < ready.Count; i++)
            {
                FileItemViewModel item = ready[i];
                if (!Files.Contains(item))
                {
                    continue; // removed from the list while waiting
                }

                SelectedFile = item;
                SetStatus($"Transcribing {item.FileName} ({i + 1} of {ready.Count})…");
                var observer = new FileItemObserver(item, dispatcher);
                try
                {
                    // Run off the UI thread: reading audio and preparing chunks is synchronous work.
                    TranscriptionJobResult result = await Task.Run(
                        () => runner.RunAsync(item.FullPath, item.Media!, directories[i], baseNames[i], options, observer, token), token);
                    item.Complete(result);
                    succeeded++;
                    AddLog($"{item.FileName}: saved {string.Join(", ", result.WrittenFiles)}");
                }
                catch (UserFacingException ex)
                {
                    item.Fail(ex.Message, ex.Hint);
                    AddLog($"{item.FileName}: {ex.Message}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    item.Fail($"Unexpected error: {ex.Message}", "Please report this on the project's GitHub page.");
                    AddLog($"{item.FileName}: {ex}");
                }
            }

            int failed = queue.Count(f => Files.Contains(f) && f.Status != FileStatus.Done);
            if (failed == 0)
            {
                SetStatus(succeeded == 1 ? "Done! The transcript was saved." : $"Done! {succeeded} transcripts were saved.");
            }
            else
            {
                SetStatus($"Finished: {succeeded} succeeded, {failed} failed. See the messages next to each file.", isError: true);
            }

            return failed == 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (FileItemViewModel item in queue.Where(f => f.IsActive))
            {
                item.Status = FileStatus.Cancelled;
            }

            SetStatus("Cancelled.");
            return false;
        }
        catch (UserFacingException ex)
        {
            foreach (FileItemViewModel item in queue.Where(f => f.IsActive))
            {
                item.Status = FileStatus.Waiting;
            }

            SetStatus(ex.Message, isError: true);
            AddLog(ex.Message + (ex.Hint is null ? string.Empty : Environment.NewLine + ex.Hint));
            await dialogs.ShowMessageAsync("VideoTranscriber", ex.Hint is null ? ex.Message : $"{ex.Message}\n\n{ex.Hint}", isError: true);
            return false;
        }
        catch (Exception ex)
        {
            foreach (FileItemViewModel item in queue.Where(f => f.IsActive))
            {
                item.Fail($"Unexpected error: {ex.Message}", null);
            }

            SetStatus($"Unexpected error: {ex.Message}", isError: true);
            AddLog(ex.ToString());
            await dialogs.ShowMessageAsync("Unexpected error", $"{ex.Message}\n\nDetails are in the Log tab. Please report this on the project's GitHub page.", isError: true);
            return false;
        }
        finally
        {
            ActivityLabel = null;
            IsBusy = false;
            cancellation.Dispose();
            cancellation = null;
        }
    }

    /// <summary>Used by "--smoke-test": transcribe one file with the tiny model and report success.</summary>
    public async Task<bool> RunSmokeTestAsync(string video, string outputFolder)
    {
        Files.Clear();
        AddFiles([video]);
        SelectedModel = Models[0];
        FormatTxt = FormatSrt = FormatVtt = FormatJson = true;
        SaveNextToVideo = false;
        OutputFolder = outputFolder;
        return await RunQueueAsync() && Files.All(f => f.IsDone);
    }

    public void Shutdown()
    {
        cancellation?.Cancel();
        SaveSettings();
        transcriber?.Dispose();
        httpClient.Dispose();
    }

    private async Task LoadTranscriberAsync(WhisperModelInfo model, string modelPath, bool cpuOnly, IStatusReporter status)
    {
        if (transcriber is not null && loadedTranscriber == (modelPath, cpuOnly))
        {
            return;
        }

        transcriber?.Dispose();
        transcriber = null;
        SetStatus($"Loading the {model.Name} model…");
        transcriber = await Task.Run(() => Transcriber.Load(modelPath, cpuOnly, status, nativeLog: null));
        loadedTranscriber = (modelPath, cpuOnly);
        EngineInfo = $"Model: {model.Name} · Running on {transcriber.RuntimeDescription}";
        AddLog(EngineInfo);
    }

    // ---------------------------------------------------------------- Settings and helpers

    public IReadOnlyList<OutputFormat> SelectedFormats()
    {
        var formats = new List<OutputFormat>();
        if (FormatTxt) formats.Add(OutputFormat.Txt);
        if (FormatSrt) formats.Add(OutputFormat.Srt);
        if (FormatVtt) formats.Add(OutputFormat.Vtt);
        if (FormatJson) formats.Add(OutputFormat.Json);
        return formats;
    }

    internal void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    internal void AddLog(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 500)
        {
            Log.RemoveAt(0);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        SelectedModel = Models.FirstOrDefault(m => m.Model.Name == settings.Model) ?? Models[1];
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == settings.Language) ?? Languages[0];
        Translate = settings.Translate;
        FormatTxt = settings.Txt;
        FormatSrt = settings.Srt;
        FormatVtt = settings.Vtt;
        FormatJson = settings.Json;
        OutputFolder = settings.OutputFolder;
        SaveNextToVideo = settings.SaveNextToVideo || string.IsNullOrWhiteSpace(settings.OutputFolder);
        UseGpu = settings.UseGpu;
    }

    private void SaveSettings() => settingsStore.Save(new AppSettings
    {
        Model = SelectedModel.Model.Name,
        Language = SelectedLanguage.Code ?? TranscriptionSettings.AutoDetect,
        Translate = Translate,
        Txt = FormatTxt,
        Srt = FormatSrt,
        Vtt = FormatVtt,
        Json = FormatJson,
        SaveNextToVideo = SaveNextToVideo,
        OutputFolder = OutputFolder,
        UseGpu = UseGpu,
    });

    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanClearFinished));
        StartCommand.NotifyCanExecuteChanged();
        if (Files.Count == 0 && !IsBusy)
        {
            SetStatus("Add videos to get started.");
        }
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItemViewModel.Status))
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanClearFinished));
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private static bool IsLikelyMediaFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".flv"
            or ".m4v" or ".mpg" or ".mpeg" or ".ts" or ".3gp" or ".mp3" or ".m4a" or ".wav" or ".flac" or ".ogg" or ".opus"
            or ".aac" or ".wma";

    /// <summary>Forwards one file's progress (reported on background threads) to its row in the list.</summary>
    private sealed class FileItemObserver : ITranscriptionJobObserver
    {
        private readonly FileItemViewModel item;
        private readonly IUiDispatcher dispatcher;
        private int lastPercent = -1;

        public FileItemObserver(FileItemViewModel item, IUiDispatcher dispatcher)
        {
            this.item = item;
            this.dispatcher = dispatcher;
        }

        public void StageStarted(JobStage stage)
        {
            lastPercent = -1;
            dispatcher.Post(() => item.StartStage(stage));
        }

        public void StageProgress(JobStage stage, double fraction)
        {
            // Only update the UI when the whole-percent value changes.
            int percent = (int)Math.Clamp(fraction * 100, 0, 100);
            if (Interlocked.Exchange(ref lastPercent, percent) != percent)
            {
                dispatcher.Post(() => item.Progress = percent);
            }
        }

        public void StageCompleted(JobStage stage) => dispatcher.Post(() => item.Progress = 100);

        public void SegmentRecognized(TranscriptSegment segment) => dispatcher.Post(() => item.AppendSegment(segment));
    }
}
