using System.IO.Compression;
using FFMpegCore;
using VideoTranscriber.Infrastructure;

namespace VideoTranscriber.Ffmpeg;

/// <summary>Makes sure FFmpeg is available, offering a one-time automatic download on Windows.</summary>
public sealed class FfmpegSetup
{
    /// <summary>Static Windows x64 build of FFmpeg published on GitHub by BtbN (GPL, includes ffmpeg and ffprobe).</summary>
    public static readonly Uri WindowsDownloadUrl =
        new("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip");

    private readonly FileDownloader downloader;
    private readonly IStatusReporter status;

    public FfmpegSetup(FileDownloader downloader, IStatusReporter status)
    {
        this.downloader = downloader;
        this.status = status;
    }

    /// <summary>Locates FFmpeg (installing it if the user agrees) and configures FFMpegCore to use it.</summary>
    public async Task<string> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        string? folder = FfmpegLocator.FindBinaryFolder(out string? ffmpegOnlyFolder);

        if (folder is null && ffmpegOnlyFolder is not null)
        {
            status.Warning($"Found {FfmpegLocator.FfmpegFileName} in {ffmpegOnlyFolder}, but {FfmpegLocator.FfprobeFileName} is missing from that folder.");
        }

        if (folder is null && CanAutoInstall &&
            await status.ConfirmAsync("FFmpeg is required but wasn't found. Download it now (about 200 MB, one time only)?", defaultAnswer: true))
        {
            folder = await InstallAsync(cancellationToken);
        }

        if (folder is null)
        {
            throw new UserFacingException(
                "FFmpeg was not found. VideoTranscriber needs ffmpeg and ffprobe to read video files.",
                FfmpegLocator.InstallInstructions());
        }

        GlobalFFOptions.Configure(options => options.BinaryFolder = folder);
        return folder;
    }

    /// <summary>The automatic download is a Windows x64 build.</summary>
    public static bool CanAutoInstall => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem;

    private async Task<string> InstallAsync(CancellationToken cancellationToken)
    {
        string targetFolder = AppPaths.FfmpegDirectory;
        string zipPath = Path.Combine(Path.GetTempPath(), $"videotranscriber-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            status.Step("Downloading FFmpeg...");
            await downloader.DownloadAsync(WindowsDownloadUrl, zipPath, "Downloading", cancellationToken);

            status.Step("Unpacking FFmpeg...");
            Directory.CreateDirectory(targetFolder);
            ExtractTools(zipPath, targetFolder);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            throw new UserFacingException(
                $"Couldn't download FFmpeg: {ex.Message}",
                "Check your internet connection and try again." + Environment.NewLine + FfmpegLocator.InstallInstructions(),
                ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new UserFacingException(
                $"Couldn't install FFmpeg into {targetFolder}: {ex.Message}",
                FfmpegLocator.InstallInstructions(),
                ex);
        }
        finally
        {
            try
            {
                File.Delete(zipPath);
            }
            catch (IOException)
            {
            }
        }

        status.Success($"FFmpeg installed to {targetFolder}");
        return targetFolder;
    }

    private static void ExtractTools(string zipPath, string targetFolder)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (string toolName in new[] { FfmpegLocator.FfmpegFileName, FfmpegLocator.FfprobeFileName })
        {
            // The archive layout is <build-name>/bin/ffmpeg.exe.
            ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.Name, toolName, StringComparison.OrdinalIgnoreCase) &&
                    e.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{toolName} was not found in the downloaded archive.");

            entry.ExtractToFile(Path.Combine(targetFolder, toolName), overwrite: true);
        }
    }
}
