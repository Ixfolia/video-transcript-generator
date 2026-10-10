using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VideoTranscriber.Gui.Views;

namespace VideoTranscriber.Gui.Services;

/// <summary>Everything the view model needs from the window: pickers, message boxes, clipboard, opening files.</summary>
public interface IDialogService
{
    Task<IReadOnlyList<string>> PickVideosAsync();

    Task<string?> PickFolderAsync(string? startFolder);

    Task<bool> ConfirmAsync(string title, string message, string yesText, string noText);

    Task ShowMessageAsync(string title, string message, bool isError);

    Task CopyToClipboardAsync(string text);

    void OpenFile(string path);

    void ShowInFolder(string path);
}

public sealed class DialogService : IDialogService
{
    private static readonly string[] VideoPatterns =
    [
        "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.wmv", "*.flv", "*.m4v", "*.mpg", "*.mpeg", "*.ts", "*.3gp",
        "*.mp3", "*.m4a", "*.wav", "*.flac", "*.ogg", "*.opus", "*.aac", "*.wma",
    ];

    private readonly Window owner;

    public DialogService(Window owner)
    {
        this.owner = owner;
    }

    public async Task<IReadOnlyList<string>> PickVideosAsync()
    {
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose videos to transcribe",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Video and audio files") { Patterns = VideoPatterns },
                FilePickerFileTypes.All,
            ],
        });

        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string? startFolder)
    {
        IStorageFolder? start = startFolder is not null && Directory.Exists(startFolder)
            ? await owner.StorageProvider.TryGetFolderFromPathAsync(startFolder)
            : null;

        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to save transcripts",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string yesText, string noText) =>
        MessageDialog.ShowAsync(owner, title, message, yesText, noText, isError: false);

    public async Task ShowMessageAsync(string title, string message, bool isError) =>
        await MessageDialog.ShowAsync(owner, title, message, "OK", null, isError);

    public async Task CopyToClipboardAsync(string text)
    {
        if (owner.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public void OpenFile(string path) => Launch(path, arguments: null);

    public void ShowInFolder(string path)
    {
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            // Opens Explorer with the file selected.
            Launch("explorer.exe", $"/select,\"{path}\"");
        }
        else
        {
            Launch(File.Exists(path) ? Path.GetDirectoryName(path)! : path, arguments: null);
        }
    }

    private static void Launch(string fileName, string? arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = true };
            if (arguments is not null)
            {
                startInfo.Arguments = arguments;
            }

            Process.Start(startInfo)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            // Nothing sensible to do if the shell can't open it (e.g. no app registered for .srt).
        }
    }
}
