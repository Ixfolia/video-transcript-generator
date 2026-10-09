namespace VideoTranscriber.Infrastructure;

/// <summary>Well-known folders used by the app.</summary>
public static class AppPaths
{
    /// <summary>Overrides the model cache folder (useful for portable installs).</summary>
    public const string ModelDirectoryVariable = "VIDEOTRANSCRIBER_MODELS";

    /// <summary><c>%LOCALAPPDATA%\VideoTranscriber</c> on Windows, <c>~/.local/share/VideoTranscriber</c> elsewhere.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "VideoTranscriber");

    public static string ModelDirectory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable(ModelDirectoryVariable);
            return string.IsNullOrWhiteSpace(overridden) ? Path.Combine(DataDirectory, "models") : overridden;
        }
    }

    /// <summary>Where the automatic FFmpeg download is installed.</summary>
    public static string FfmpegDirectory => Path.Combine(DataDirectory, "ffmpeg");

    /// <summary>
    /// Folder containing VideoTranscriber.exe. For a single-file app this differs from <see cref="AppContext.BaseDirectory"/>,
    /// which points at the temporary extraction folder.
    /// </summary>
    public static string? ExecutableDirectory =>
        Environment.ProcessPath is { } processPath ? Path.GetDirectoryName(processPath) : null;
}
