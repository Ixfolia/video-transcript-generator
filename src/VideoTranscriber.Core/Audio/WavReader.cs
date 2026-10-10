using System.Buffers.Binary;
using System.Text;

namespace VideoTranscriber.Audio;

/// <summary>
/// Streams 16-bit PCM samples from a WAV file as floats in [-1, 1) without loading the whole file into memory.
/// </summary>
public sealed class WavReader : IDisposable
{
    private const ushort FormatPcm = 1;
    private const ushort FormatExtensible = 0xFFFE;

    private readonly Stream stream;
    private readonly byte[] byteBuffer = new byte[64 * 1024];
    private long remainingBytes;

    private WavReader(Stream stream, int sampleRate, int channels, long dataBytes)
    {
        this.stream = stream;
        SampleRate = sampleRate;
        Channels = channels;
        remainingBytes = dataBytes;
        SampleCount = dataBytes / 2 / channels;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Number of samples per channel.</summary>
    public long SampleCount { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(SampleCount / (double)SampleRate);

    public static WavReader Open(string path) => Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

    /// <summary>Parses the WAV header. Takes ownership of <paramref name="stream"/>.</summary>
    public static WavReader Open(Stream stream)
    {
        try
        {
            return Parse(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads mono samples into <paramref name="destination"/>. Multi-channel audio is down-mixed. Returns the number
    /// of samples written; 0 means the end of the audio.
    /// </summary>
    public int Read(Span<float> destination)
    {
        int frameBytes = 2 * Channels;
        int written = 0;
        while (written < destination.Length && remainingBytes >= frameBytes)
        {
            long wantedBytes = Math.Min((long)(destination.Length - written) * frameBytes, remainingBytes);
            int toRead = (int)Math.Min(wantedBytes, byteBuffer.Length - (byteBuffer.Length % frameBytes));
            int read = ReadFully(byteBuffer.AsSpan(0, toRead));
            int frames = read / frameBytes;
            if (frames == 0)
            {
                remainingBytes = 0;
                break;
            }

            for (int frame = 0; frame < frames; frame++)
            {
                int sum = 0;
                for (int channel = 0; channel < Channels; channel++)
                {
                    sum += BinaryPrimitives.ReadInt16LittleEndian(byteBuffer.AsSpan((frame * Channels + channel) * 2, 2));
                }

                destination[written++] = sum / (32768f * Channels);
            }

            remainingBytes -= read;
            if (read < toRead)
            {
                remainingBytes = 0;
            }
        }

        return written;
    }

    public void Dispose() => stream.Dispose();

    private static WavReader Parse(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        // RF64 is the 64-bit variant FFmpeg can write for files over 4 GB; its sizes live in a "ds64" chunk (skipped
        // below) and the data chunk size is 0xFFFFFFFF, which is handled as "until end of file".
        if (ReadFully(stream, header) < 12
            || !(header[..4].SequenceEqual("RIFF"u8) || header[..4].SequenceEqual("RF64"u8))
            || !header[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a WAV file (missing RIFF/WAVE header).");
        }

        int? sampleRate = null;
        int channels = 0;
        Span<byte> chunkHeader = stackalloc byte[8];
        while (ReadFully(stream, chunkHeader) == 8)
        {
            string chunkId = Encoding.ASCII.GetString(chunkHeader[..4]);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("Invalid WAV format chunk.");
                }

                byte[] fmt = new byte[chunkSize];
                if (ReadFully(stream, fmt) < fmt.Length)
                {
                    throw new InvalidDataException("Truncated WAV format chunk.");
                }

                ushort audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                if (audioFormat is not (FormatPcm or FormatExtensible) || bitsPerSample != 16 || channels < 1 || sampleRate <= 0)
                {
                    throw new InvalidDataException($"Unsupported WAV encoding (format {audioFormat}, {bitsPerSample}-bit). Expected 16-bit PCM.");
                }

                SkipPadding(stream, chunkSize);
            }
            else if (chunkId == "data")
            {
                if (sampleRate is null)
                {
                    throw new InvalidDataException("WAV data chunk appears before the format chunk.");
                }

                // Streaming writers may leave the size as 0 or 0xFFFFFFFF, and the 32-bit size field overflows for
                // audio over 4 GB (~37 hours at 16 kHz). In those cases read until the end of the file.
                long dataBytes = chunkSize;
                if (stream.CanSeek)
                {
                    long available = stream.Length - stream.Position;
                    if (chunkSize == 0 || chunkSize == uint.MaxValue || dataBytes > available || available > uint.MaxValue)
                    {
                        dataBytes = available;
                    }
                }

                return new WavReader(stream, sampleRate.Value, channels, dataBytes);
            }
            else
            {
                Skip(stream, chunkSize);
                SkipPadding(stream, chunkSize);
            }
        }

        throw new InvalidDataException("WAV file has no audio data.");
    }

    private int ReadFully(Span<byte> buffer) => ReadFully(stream, buffer);

    private static int ReadFully(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void SkipPadding(Stream stream, uint chunkSize)
    {
        // RIFF chunks are word-aligned.
        if (chunkSize % 2 == 1)
        {
            Skip(stream, 1);
        }
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> scratch = stackalloc byte[4096];
        while (count > 0)
        {
            int read = stream.Read(scratch[..(int)Math.Min(scratch.Length, count)]);
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }
}
