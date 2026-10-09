using VideoTranscriber.Infrastructure;

namespace VideoTranscriber.Ffmpeg;

/// <summary>Finds a folder containing both ffmpeg and ffprobe.</summary>
public static class FfmpegLocator
{
    private static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    public static string FfmpegFileName => "ffmpeg" + ExecutableSuffix;

    public static string FfprobeFileName => "ffprobe" + ExecutableSuffix;

    /// <summary>
    /// Search order: next to VideoTranscriber.exe, the app's own FFmpeg folder (automatic download), then PATH.
    /// Returns null when no folder has both tools; <paramref name="ffmpegOnlyFolder"/> reports a folder that has
    /// ffmpeg without ffprobe, to give a precise error.
    /// </summary>
    public static string? FindBinaryFolder(out string? ffmpegOnlyFolder)
    {
        ffmpegOnlyFolder = null;
        foreach (string folder in CandidateFolders())
        {
            bool hasFfmpeg = File.Exists(Path.Combine(folder, FfmpegFileName));
            if (!hasFfmpeg)
            {
                continue;
            }

            if (File.Exists(Path.Combine(folder, FfprobeFileName)))
            {
                return folder;
            }

            ffmpegOnlyFolder ??= folder;
        }

        return null;
    }

    private static IEnumerable<string> CandidateFolders()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        IEnumerable<string?> folders =
        [
            AppPaths.ExecutableDirectory,
            AppPaths.ExecutableDirectory is { } exeDir ? Path.Combine(exeDir, "ffmpeg") : null,
            AppPaths.FfmpegDirectory,
            .. (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator),
        ];

        foreach (string? folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            string trimmed = folder.Trim().Trim('"');
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(trimmed);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (seen.Add(fullPath))
            {
                yield return fullPath;
            }
        }
    }

    public static string InstallInstructions()
    {
        if (OperatingSystem.IsWindows())
        {
            return string.Join(Environment.NewLine,
                "Install FFmpeg in one of these ways:",
                "  1. Run VideoTranscriber again and answer Y when it offers to download FFmpeg, or",
                "  2. In a terminal run:  winget install --id Gyan.FFmpeg  (then open a new terminal window), or",
                "  3. Download a build from https://www.gyan.dev/ffmpeg/builds/ or https://github.com/BtbN/FFmpeg-Builds/releases",
                "     and copy ffmpeg.exe and ffprobe.exe (from its bin folder) next to VideoTranscriber.exe.");
        }

        if (OperatingSystem.IsMacOS())
        {
            return "Install FFmpeg with Homebrew:  brew install ffmpeg";
        }

        return "Install FFmpeg with your package manager, for example:  sudo apt install ffmpeg";
    }
}
