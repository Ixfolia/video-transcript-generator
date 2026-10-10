using VideoTranscriber.Gui;
using VideoTranscriber.Gui.Services;
using VideoTranscriber.Gui.ViewModels;
using VideoTranscriber.Output;
using VideoTranscriber.Transcription;

namespace VideoTranscriber.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "vt-gui-" + Guid.NewGuid().ToString("N"));

    public MainWindowViewModelTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private static MainWindowViewModel CreateViewModel(SettingsStore? store = null) =>
        new(new NoDialogs(), store ?? new SettingsStore(path: null), new ImmediateDispatcher());

    private string CreateFile(string name)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    [Fact]
    public void AddFiles_SkipsMissingFilesAndDuplicates()
    {
        MainWindowViewModel vm = CreateViewModel();
        string video = CreateFile("talk.mp4");

        vm.AddFiles([video, video, Path.Combine(folder, "missing.mp4")]);

        FileItemViewModel item = Assert.Single(vm.Files);
        Assert.Equal("talk.mp4", item.FileName);
        Assert.Equal(FileStatus.Waiting, item.Status);
    }

    [Fact]
    public void AddFiles_ExpandsADroppedFolderToItsMediaFiles()
    {
        MainWindowViewModel vm = CreateViewModel();
        CreateFile("b.mkv");
        CreateFile("a.mp4");
        CreateFile("notes.txt");

        vm.AddFiles([folder]);

        Assert.Equal(["a.mp4", "b.mkv"], vm.Files.Select(f => f.FileName));
    }

    [Fact]
    public void CanStart_NeedsFilesAndAtLeastOneFormat()
    {
        MainWindowViewModel vm = CreateViewModel();
        Assert.False(vm.CanStart);

        vm.AddFiles([CreateFile("talk.mp4")]);
        Assert.True(vm.CanStart);

        vm.FormatTxt = vm.FormatSrt = vm.FormatVtt = vm.FormatJson = false;
        Assert.False(vm.CanStart);
        Assert.False(vm.StartCommand.CanExecute(null));

        vm.FormatVtt = true;
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal([OutputFormat.Vtt], vm.SelectedFormats());
    }

    [Fact]
    public void RemoveCommand_RemovesAWaitingFile()
    {
        MainWindowViewModel vm = CreateViewModel();
        vm.AddFiles([CreateFile("talk.mp4")]);

        vm.Files[0].RemoveCommand.Execute(null);

        Assert.Empty(vm.Files);
        Assert.False(vm.HasFiles);
    }

    [Fact]
    public void DefaultsMatchTheCommandLine()
    {
        MainWindowViewModel vm = CreateViewModel();

        Assert.Equal("base", vm.SelectedModel.Model.Name);
        Assert.Null(vm.SelectedLanguage.Code);
        Assert.Equal([OutputFormat.Txt, OutputFormat.Srt], vm.SelectedFormats());
        Assert.True(vm.SaveNextToVideo);
        Assert.True(vm.UseGpu);
    }

    [Fact]
    public void SettingsAreRestored()
    {
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));
        store.Save(new AppSettings
        {
            Model = "small", Language = "de", Translate = true, Txt = false, Srt = false, Vtt = true, Json = true,
            SaveNextToVideo = false, OutputFolder = folder, UseGpu = false,
        });

        MainWindowViewModel vm = CreateViewModel(store);

        Assert.Equal("small", vm.SelectedModel.Model.Name);
        Assert.Equal("de", vm.SelectedLanguage.Code);
        Assert.True(vm.Translate);
        Assert.Equal([OutputFormat.Vtt, OutputFormat.Json], vm.SelectedFormats());
        Assert.False(vm.SaveNextToVideo);
        Assert.Equal(folder, vm.OutputFolder);
        Assert.False(vm.UseGpu);
    }

    [Fact]
    public void DamagedSettingsFileFallsBackToDefaults()
    {
        string path = Path.Combine(folder, "settings.json");
        File.WriteAllText(path, "{ not json");

        AppSettings settings = new SettingsStore(path).Load();

        Assert.Equal("base", settings.Model);
        Assert.True(settings.Txt);
    }

    private sealed class NoDialogs : IDialogService
    {
        public Task<IReadOnlyList<string>> PickVideosAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickFolderAsync(string? startFolder) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string yesText, string noText) => Task.FromResult(false);
        public Task ShowMessageAsync(string title, string message, bool isError) => Task.CompletedTask;
        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
        public void OpenFile(string path) { }
        public void ShowInFolder(string path) { }
    }
}

public class StartupArgumentsTests
{
    [Fact]
    public void FilesDroppedOnTheExeAreQueued()
    {
        StartupArguments args = StartupArguments.Parse([@"C:\v\a.mp4", @"C:\v\b.mkv"]);

        Assert.Equal([@"C:\v\a.mp4", @"C:\v\b.mkv"], args.Files);
        Assert.False(args.IsSmokeTest);
    }

    [Fact]
    public void SmokeTestMode()
    {
        StartupArguments args = StartupArguments.Parse(["--smoke-test", "clip.mp4", "out"]);

        Assert.True(args.IsSmokeTest);
        Assert.Equal("clip.mp4", args.SmokeTestVideo);
        Assert.Equal("out", args.SmokeTestOutput);
        Assert.Empty(args.Files);
    }
}

public class WhisperLanguagesTests
{
    [Fact]
    public void Has99UniqueLanguagesSortedByName()
    {
        Assert.Equal(99, WhisperLanguages.All.Count);
        Assert.Equal(99, WhisperLanguages.All.Select(l => l.Code).Distinct().Count());
        Assert.Equal(WhisperLanguages.All.Select(l => l.Name).Order(StringComparer.Ordinal), WhisperLanguages.All.Select(l => l.Name));
    }

    [Theory]
    [InlineData("de", "German")]
    [InlineData("EN", "English")]
    [InlineData("haw", "Hawaiian")]
    [InlineData("xx", "xx")]
    public void DisplayName(string code, string expected)
    {
        Assert.Equal(expected, WhisperLanguages.DisplayName(code));
    }
}
