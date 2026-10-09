using System.Buffers.Binary;
using System.Text;
using VideoTranscriber.Audio;

namespace VideoTranscriber.Tests;

public class WavReaderTests
{
    /// <summary>Builds a 16-bit PCM WAV file in memory, optionally with an extra chunk before the data.</summary>
    internal static MemoryStream CreateWav(short[] samples, int sampleRate = 16_000, int channels = 1, bool withListChunk = false, ushort bitsPerSample = 16)
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        byte[] list = Encoding.ASCII.GetBytes("INFOISFT\u0005\0\0\0Lavf\0\0"); // odd payload to exercise padding

        writer.Write("RIFF"u8);
        writer.Write(0); // size patched below
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((ushort)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);

        if (withListChunk)
        {
            writer.Write("LIST"u8);
            writer.Write(list.Length - 1);
            writer.Write(list.AsSpan(0, list.Length - 1));
            writer.Write((byte)0); // pad byte
        }

        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        byte[] buffer = stream.GetBuffer();
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), (int)stream.Length - 8);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void ReadsHeaderAndConvertsSamplesToFloats()
    {
        using var reader = WavReader.Open(CreateWav([0, 16384, -32768, 32767]));

        Assert.Equal(16_000, reader.SampleRate);
        Assert.Equal(1, reader.Channels);
        Assert.Equal(4, reader.SampleCount);

        float[] samples = new float[10];
        int read = reader.Read(samples);

        Assert.Equal(4, read);
        Assert.Equal(0f, samples[0]);
        Assert.Equal(0.5f, samples[1]);
        Assert.Equal(-1f, samples[2]);
        Assert.InRange(samples[3], 0.9999f, 1f);
        Assert.Equal(0, reader.Read(samples));
    }

    [Fact]
    public void SkipsUnknownChunksIncludingPadding()
    {
        using var reader = WavReader.Open(CreateWav([100, 200, 300], withListChunk: true));

        float[] samples = new float[3];
        Assert.Equal(3, reader.Read(samples));
        Assert.Equal(100 / 32768f, samples[0]);
        Assert.Equal(300 / 32768f, samples[2]);
    }

    [Fact]
    public void ReadsInSmallPiecesWithoutLosingSamples()
    {
        short[] source = Enumerable.Range(0, 100_000).Select(i => (short)(i % 30_000)).ToArray();
        using var reader = WavReader.Open(CreateWav(source));

        var all = new List<float>();
        float[] buffer = new float[777];
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            all.AddRange(buffer.Take(read));
        }

        Assert.Equal(source.Length, all.Count);
        Assert.Equal(source[^1] / 32768f, all[^1]);
    }

    [Fact]
    public void DownmixesStereoToMono()
    {
        using var reader = WavReader.Open(CreateWav([1000, 3000, -2000, -4000], channels: 2));

        float[] samples = new float[2];
        Assert.Equal(2, reader.Read(samples));
        Assert.Equal(2000 / 32768f, samples[0]);
        Assert.Equal(-3000 / 32768f, samples[1]);
    }

    [Fact]
    public void RejectsNonWavData()
    {
        Assert.Throws<InvalidDataException>(() => WavReader.Open(new MemoryStream(Encoding.ASCII.GetBytes("definitely not a wav file"))));
    }

    [Fact]
    public void RejectsUnsupportedBitDepth()
    {
        Assert.Throws<InvalidDataException>(() => WavReader.Open(CreateWav([1, 2], bitsPerSample: 8)));
    }
}

public class AudioChunkerTests
{
    [Fact]
    public void FindQuietestCut_PicksTheSilentRegion()
    {
        float[] samples = Enumerable.Repeat(0.5f, 2000).ToArray();
        Array.Fill(samples, 0f, 1200, 300); // silence at 1200..1499

        int cut = AudioChunker.FindQuietestCut(samples, searchStart: 1000, frameSize: 50, windowFrames: 4);

        Assert.InRange(cut, 1200, 1500);
    }

    [Fact]
    public void FindQuietestCut_PrefersALongPauseOverALaterShortGap()
    {
        float[] samples = Enumerable.Repeat(0.5f, 3000).ToArray();
        Array.Fill(samples, 0f, 1000, 500);  // long pause between sentences
        Array.Fill(samples, 0f, 2500, 50);   // brief gap between words, later in the window

        int cut = AudioChunker.FindQuietestCut(samples, searchStart: 500, frameSize: 50, windowFrames: 4);

        Assert.InRange(cut, 1000, 1500);
    }

    [Fact]
    public void FindQuietestCut_FallsBackToEndWhenNoFrameFits()
    {
        float[] samples = new float[10];

        Assert.Equal(10, AudioChunker.FindQuietestCut(samples, searchStart: 5, frameSize: 50));
    }

    [Fact]
    public void Chunks_CoverTheWholeStreamInOrderWithoutGapsOrOverlap()
    {
        const int sampleRate = 100;
        const int total = 10_000; // 100 seconds
        float[] source = Enumerable.Range(0, total).Select(i => (float)Math.Sin(i * 0.1) * (i % 1500 < 100 ? 0.01f : 1f)).ToArray();
        int position = 0;
        var chunker = new AudioChunker(
            destination =>
            {
                // Return at most 333 samples per call to simulate a streaming reader.
                int count = Math.Min(Math.Min(destination.Length, 333), total - position);
                source.AsSpan(position, count).CopyTo(destination);
                position += count;
                return count;
            },
            sampleRate,
            chunkLength: TimeSpan.FromSeconds(30),
            searchWindow: TimeSpan.FromSeconds(10));

        var reassembled = new List<float>();
        long expectedStart = 0;
        int chunkCount = 0;
        foreach (AudioChunk chunk in chunker.ReadChunks())
        {
            chunkCount++;
            Assert.Equal(expectedStart, chunk.StartSample);
            Assert.InRange(chunk.Samples.Length, 1, 30 * sampleRate);
            reassembled.AddRange(chunk.Samples.ToArray());
            expectedStart += chunk.Samples.Length;
        }

        Assert.True(chunkCount >= 4);
        Assert.Equal(source, reassembled);
    }

    [Fact]
    public void ShortAudioIsASingleChunk()
    {
        float[] source = new float[500];
        int position = 0;
        var chunker = new AudioChunker(
            destination =>
            {
                int count = Math.Min(destination.Length, source.Length - position);
                position += count;
                return count;
            },
            sampleRate: 100,
            chunkLength: TimeSpan.FromSeconds(30),
            searchWindow: TimeSpan.FromSeconds(10));

        AudioChunk single = Assert.Single(chunker.ReadChunks());
        Assert.Equal(500, single.Samples.Length);
        Assert.Equal(0, single.StartSample);
    }

    [Fact]
    public void EmptyAudioYieldsNoChunks()
    {
        var chunker = new AudioChunker(_ => 0, 16_000, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));

        Assert.Empty(chunker.ReadChunks());
    }
}
