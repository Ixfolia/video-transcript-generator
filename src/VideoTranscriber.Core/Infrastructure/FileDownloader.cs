using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

namespace VideoTranscriber.Infrastructure;

/// <summary>Downloads a file over HTTPS with a progress bar, writing to a <c>.part</c> file until complete.</summary>
public sealed class FileDownloader
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient httpClient;
    private readonly IStatusReporter status;

    public FileDownloader(HttpClient httpClient, IStatusReporter status)
    {
        this.httpClient = httpClient;
        this.status = status;
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        })
        {
            // Large models can take a long time on slow connections; stalls are detected per read instead.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VideoTranscriber", version));
        return client;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>. The destination only appears once the
    /// download has completed. Network failures (including a connection that drops mid-download) surface as
    /// <see cref="HttpRequestException"/> or <see cref="TimeoutException"/>; <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/> mean the file couldn't be written locally.
    /// </summary>
    public async Task DownloadAsync(Uri url, string destinationPath, string label, CancellationToken cancellationToken)
    {
        // Unique per run, so two instances downloading the same file at once don't collide.
        string partialPath = $"{destinationPath}.{Guid.NewGuid():N}.part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);

        try
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // SocketsHttpHandler reports ConnectTimeout as a cancellation, not as an HttpRequestException.
                throw new TimeoutException($"Timed out connecting to {url.Host}.", ex);
            }

            using HttpResponseMessage disposeResponse = response;
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            long receivedBytes = 0;
            var stopwatch = Stopwatch.StartNew();

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (IProgressReporter progress = status.BeginProgress(label))
            {
                byte[] buffer = new byte[1 << 16];
                while (true)
                {
                    int read;
                    using (var stallGuard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        stallGuard.CancelAfter(StallTimeout);
                        try
                        {
                            read = await source.ReadAsync(buffer, stallGuard.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException($"The download stalled (no data received for {StallTimeout.TotalSeconds:0} seconds).");
                        }
                        catch (IOException ex)
                        {
                            // HttpIOException derives from IOException; report it as a network problem, not a disk one.
                            throw new HttpRequestException(HttpRequestError.ResponseEnded, $"The download was interrupted: {ex.Message}", ex);
                        }
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    receivedBytes += read;
                    progress.Report(totalBytes > 0 ? receivedBytes / (double)totalBytes.Value : 0, Describe(receivedBytes, totalBytes, stopwatch.Elapsed));
                }

                if (totalBytes is { } expected && receivedBytes != expected)
                {
                    throw new HttpRequestException($"The connection closed early ({FormatSize(receivedBytes)} of {FormatSize(expected)} received).");
                }

                progress.Complete(Describe(receivedBytes, totalBytes, stopwatch.Elapsed));
            }

            File.Move(partialPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.00} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };

    private static string Describe(long received, long? total, TimeSpan elapsed)
    {
        string speed = elapsed.TotalSeconds > 0.5 ? $"  {FormatSize((long)(received / elapsed.TotalSeconds))}/s" : string.Empty;
        return total > 0 ? $"{FormatSize(received)} / {FormatSize(total.Value)}{speed}" : $"{FormatSize(received)}{speed}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
