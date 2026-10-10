namespace VideoTranscriber.Audio;

/// <summary>A window of audio samples and where it starts in the full recording.</summary>
public readonly record struct AudioChunk(ReadOnlyMemory<float> Samples, long StartSample);

/// <summary>
/// Splits a long audio stream into chunks of bounded size so that hours of audio never have to be held in memory
/// at once. Each cut is placed at the quietest moment near the end of the chunk, to avoid splitting a word.
/// </summary>
public sealed class AudioChunker
{
    private readonly ReadSamples read;
    private readonly int chunkSamples;
    private readonly int searchSamples;
    private readonly int frameSamples;

    public delegate int ReadSamples(Span<float> destination);

    /// <param name="read">Reads samples; returns 0 at the end of the stream.</param>
    /// <param name="sampleRate">Samples per second.</param>
    /// <param name="chunkLength">Maximum chunk length.</param>
    /// <param name="searchWindow">How far back from the chunk end to look for a quiet cut point.</param>
    public AudioChunker(ReadSamples read, int sampleRate, TimeSpan chunkLength, TimeSpan searchWindow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (searchWindow >= chunkLength)
        {
            throw new ArgumentException("The search window must be shorter than the chunk.", nameof(searchWindow));
        }

        this.read = read;
        chunkSamples = checked((int)(chunkLength.TotalSeconds * sampleRate));
        searchSamples = (int)(searchWindow.TotalSeconds * sampleRate);
        frameSamples = Math.Max(1, sampleRate / 20); // 50 ms energy frames, compared over 0.5 s windows
    }

    /// <summary>
    /// Enumerates the chunks. The returned memory is reused, so each chunk is only valid until the next one is
    /// requested.
    /// </summary>
    public IEnumerable<AudioChunk> ReadChunks()
    {
        float[] buffer = new float[chunkSamples];
        int filled = 0;
        long startSample = 0;
        bool endOfStream = false;

        while (true)
        {
            while (!endOfStream && filled < buffer.Length)
            {
                int count = read(buffer.AsSpan(filled));
                if (count == 0)
                {
                    endOfStream = true;
                }

                filled += count;
            }

            if (filled == 0)
            {
                yield break;
            }

            int cut = endOfStream ? filled : FindQuietestCut(buffer.AsSpan(0, filled), filled - searchSamples, frameSamples);
            yield return new AudioChunk(buffer.AsMemory(0, cut), startSample);

            int remaining = filled - cut;
            Array.Copy(buffer, cut, buffer, 0, remaining);
            filled = remaining;
            startSample += cut;
        }
    }

    /// <summary>
    /// Returns a cut position inside the quietest stretch of audio at or after <paramref name="searchStart"/>.
    /// Energy is summed over <paramref name="windowFrames"/> consecutive frames, so a real pause between sentences
    /// wins over a brief gap between words. Falls back to the end of <paramref name="samples"/> if no window fits.
    /// </summary>
    public static int FindQuietestCut(ReadOnlySpan<float> samples, int searchStart, int frameSize, int windowFrames = 10)
    {
        searchStart = Math.Clamp(searchStart, 0, samples.Length);
        int frameCount = (samples.Length - searchStart) / frameSize;
        if (frameCount == 0)
        {
            return samples.Length;
        }

        var frameEnergy = new double[frameCount];
        for (int frame = 0; frame < frameCount; frame++)
        {
            double energy = 0;
            foreach (float sample in samples.Slice(searchStart + frame * frameSize, frameSize))
            {
                energy += sample * sample;
            }

            frameEnergy[frame] = energy;
        }

        windowFrames = Math.Clamp(windowFrames, 1, frameCount);
        double windowEnergy = 0;
        for (int frame = 0; frame < windowFrames; frame++)
        {
            windowEnergy += frameEnergy[frame];
        }

        int bestFirstFrame = 0;
        double bestEnergy = windowEnergy;
        for (int first = 1; first + windowFrames <= frameCount; first++)
        {
            windowEnergy += frameEnergy[first + windowFrames - 1] - frameEnergy[first - 1];

            // "<=" prefers the latest of equally quiet windows, keeping chunks as long as possible.
            if (windowEnergy <= bestEnergy + 1e-12)
            {
                bestEnergy = windowEnergy;
                bestFirstFrame = first;
            }
        }

        // Cut in the middle of the quiet window.
        return searchStart + bestFirstFrame * frameSize + windowFrames * frameSize / 2;
    }
}
