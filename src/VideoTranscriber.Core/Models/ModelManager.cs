using System.Net;
using System.Net.Sockets;
using VideoTranscriber.Infrastructure;

namespace VideoTranscriber.Models;

/// <summary>Finds Whisper models in the local cache and downloads them on first use.</summary>
public sealed class ModelManager
{
    // GGML model files start with the magic number 0x67676d6c ("ggml") stored little-endian.
    private static readonly byte[] GgmlMagic = "lmgg"u8.ToArray();

    private readonly string cacheDirectory;
    private readonly FileDownloader downloader;
    private readonly IStatusReporter status;

    public ModelManager(string cacheDirectory, FileDownloader downloader, IStatusReporter status)
    {
        this.cacheDirectory = cacheDirectory;
        this.downloader = downloader;
        this.status = status;
    }

    public string CacheDirectory => cacheDirectory;

    public string GetModelPath(WhisperModelInfo model) => Path.Combine(cacheDirectory, model.FileName);

    /// <summary>Returns the path of a usable model file, downloading it first if needed.</summary>
    public async Task<string> EnsureModelAsync(WhisperModelInfo model, CancellationToken cancellationToken)
    {
        string path = GetModelPath(model);
        if (IsValidModelFile(path))
        {
            return path;
        }

        if (File.Exists(path))
        {
            status.Warning($"The cached model at {path} is damaged and will be downloaded again.");
            File.Delete(path);
        }

        status.Step($"Downloading the '{model.Name}' Whisper model (~{FileDownloader.FormatSize(model.ApproximateBytes)}). This only happens once.");
        status.Detail($"Saving to {path}");

        Exception? lastError = null;
        foreach (Uri url in model.DownloadUrls)
        {
            try
            {
                await downloader.DownloadAsync(url, path, "Downloading", cancellationToken);
                if (IsValidModelFile(path))
                {
                    return path;
                }

                File.Delete(path);
                lastError = new InvalidDataException("The downloaded file is not a valid Whisper GGML model.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidDataException)
            {
                lastError = ex;
                status.Warning($"Download from {url.Host} failed: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UserFacingException(
                    $"Couldn't save the model to {path}: {ex.Message}",
                    "Check that the disk has enough free space (" + FileDownloader.FormatSize(model.ApproximateBytes) + " needed) and that the folder is writable.",
                    ex);
            }
        }

        throw CreateDownloadFailure(model, path, lastError);
    }

    /// <summary>Checks that the file exists, isn't truncated to almost nothing, and starts with the GGML magic.</summary>
    public static bool IsValidModelFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1024 * 1024)
            {
                return false;
            }

            using FileStream stream = info.OpenRead();
            Span<byte> header = stackalloc byte[4];
            return stream.Read(header) == 4 && header.SequenceEqual(GgmlMagic);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static UserFacingException CreateDownloadFailure(WhisperModelInfo model, string path, Exception? error)
    {
        string manualHint =
            $"You can also download it manually from{Environment.NewLine}  {model.DownloadUrls[0]}{Environment.NewLine}" +
            $"and save it as{Environment.NewLine}  {path}";

        if (IsNoNetwork(error))
        {
            return new UserFacingException(
                $"Couldn't download the '{model.Name}' model: no internet connection (or huggingface.co is blocked).",
                "An internet connection is needed the first time each model is used; after that VideoTranscriber works offline." +
                Environment.NewLine + manualHint,
                error);
        }

        return new UserFacingException(
            $"Couldn't download the '{model.Name}' model: {error?.Message ?? "unknown error"}",
            "Try again in a few minutes. " + manualHint,
            error);
    }

    /// <summary>True when the server couldn't be reached at all (as opposed to a failure partway through).</summary>
    private static bool IsNoNetwork(Exception? error) => error switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError } => true,
        HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired } => true,
        HttpRequestException { HttpRequestError: HttpRequestError.Unknown, StatusCode: null, InnerException: SocketException } => true,
        _ => false,
    };
}
