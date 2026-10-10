using System.Text.RegularExpressions;
using VideoTranscriber.Audio;
using VideoTranscriber.Infrastructure;
using VideoTranscriber.Output;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace VideoTranscriber.Transcription;

public sealed record TranscriptionSettings(string Language, bool Translate, int Threads)
{
    public const string AutoDetect = "auto";

    public bool AutoDetectLanguage => string.Equals(Language, AutoDetect, StringComparison.OrdinalIgnoreCase);
}

public sealed record TranscriptionResult(IReadOnlyList<TranscriptSegment> Segments, string? Language);

/// <summary>Runs Whisper (via Whisper.net / whisper.cpp) over a 16 kHz mono WAV file.</summary>
public sealed partial class Transcriber : IDisposable
{
    // Audio is fed to Whisper in chunks of at most this length, so memory stays bounded for long videos
    // (10 minutes of 16 kHz float samples is ~38 MB).
    private static readonly TimeSpan ChunkLength = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CutSearchWindow = TimeSpan.FromSeconds(20);

    private readonly WhisperFactory factory;
    private readonly string? cpuFallbackReason;

    private Transcriber(WhisperFactory factory, RuntimeLibrary? runtime, string? gpuDevice, string? cpuFallbackReason)
    {
        this.factory = factory;
        Runtime = runtime;
        GpuDevice = gpuDevice;
        this.cpuFallbackReason = cpuFallbackReason;
    }

    /// <summary>The native runtime that was loaded (CUDA, Vulkan, CPU...).</summary>
    public RuntimeLibrary? Runtime { get; }

    /// <summary>The GPU whisper.cpp reported using, when it could be determined.</summary>
    public string? GpuDevice { get; }

    public string RuntimeDescription =>
        cpuFallbackReason is not null ? $"CPU ({cpuFallbackReason})"
        : GpuDevice is null ? NativeRuntime.Describe(Runtime)
        : $"{NativeRuntime.Describe(Runtime)}: {GpuDevice}";

    /// <summary>Loads the native runtime and the model.</summary>
    /// <param name="modelPath">Path to a GGML model file.</param>
    /// <param name="cpuOnly">Skip GPU runtimes.</param>
    /// <param name="nativeLog">Receives whisper.cpp's own log lines (for --verbose).</param>
    public static Transcriber Load(string modelPath, bool cpuOnly, Action<string>? nativeLog)
    {
        NativeRuntime.Configure(cpuOnly);

        var gpuDetector = new GpuDeviceDetector();
        using IDisposable logSubscription = LogProvider.AddLogger((level, message) =>
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            gpuDetector.Observe(message);
            nativeLog?.Invoke($"[whisper {level}] {message.TrimEnd()}");
        });

        WhisperFactory factory;
        string? cpuFallbackReason = null;
        try
        {
            factory = CreateFactory(modelPath, useGpu: !cpuOnly);
        }
        catch (WhisperModelLoadException) when (!cpuOnly)
        {
            // Most often the GPU ran out of memory (e.g. the medium model on a small graphics card). The CPU usually
            // has more memory to work with, so try again there before giving up.
            ConsoleUi.Warning("The model couldn't be loaded on the GPU; retrying on the CPU (slower).");
            gpuDetector = new GpuDeviceDetector();
            try
            {
                factory = CreateFactory(modelPath, useGpu: false);
                cpuFallbackReason = "the model didn't fit on the GPU";
            }
            catch (WhisperModelLoadException ex)
            {
                throw ModelLoadFailure(modelPath, ex);
            }
        }
        catch (WhisperModelLoadException ex)
        {
            throw ModelLoadFailure(modelPath, ex);
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw NativeLoadFailure(ex);
        }

        RuntimeLibrary? runtime = RuntimeOptions.LoadedLibrary;
        bool gpuRuntime = runtime is RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 or RuntimeLibrary.Vulkan;
        if (cpuFallbackReason is null && gpuRuntime && gpuDetector.ModelBuffer is not null && !gpuDetector.ModelOnGpu)
        {
            cpuFallbackReason = "no compatible GPU found";
        }

        return new Transcriber(factory, runtime, gpuDetector.Device, cpuFallbackReason);
    }

    /// <summary>Language codes Whisper understands (e.g. "en", "de", "ja").</summary>
    public static IReadOnlySet<string> SupportedLanguages() =>
        WhisperFactory.GetSupportedLanguages().ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<TranscriptionResult> TranscribeAsync(
        string wavPath,
        TranscriptionSettings settings,
        Action<double>? onProgress,
        Action<TranscriptSegment>? onSegment,
        CancellationToken cancellationToken)
    {
        using WavReader reader = WavReader.Open(wavPath);
        if (reader.SampleRate != AudioExtractor.WhisperSampleRate)
        {
            throw new InvalidDataException($"Expected {AudioExtractor.WhisperSampleRate} Hz audio but got {reader.SampleRate} Hz.");
        }

        long totalSamples = Math.Max(1, reader.SampleCount);
        long chunkStart = 0;
        int chunkLength = 0;

        WhisperProcessorBuilder builder = factory.CreateBuilder()
            .WithThreads(settings.Threads)
            .WithLanguage(settings.AutoDetectLanguage ? TranscriptionSettings.AutoDetect : settings.Language.ToLowerInvariant())
            .WithProgressHandler(percent =>
                onProgress?.Invoke((Volatile.Read(ref chunkStart) + Volatile.Read(ref chunkLength) * percent / 100.0) / totalSamples));

        if (settings.Translate)
        {
            builder = builder.WithTranslate();
        }

        await using WhisperProcessor processor = builder.Build();

        var segments = new List<TranscriptSegment>();
        string? language = settings.AutoDetectLanguage ? null : settings.Language.ToLowerInvariant();
        bool languageLocked = !settings.AutoDetectLanguage;

        var chunker = new AudioChunker(reader.Read, reader.SampleRate, ChunkLength, CutSearchWindow);
        foreach (AudioChunk chunk in chunker.ReadChunks())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref chunkStart, chunk.StartSample);
            Volatile.Write(ref chunkLength, chunk.Samples.Length);

            TimeSpan offset = SamplesToTime(chunk.StartSample, reader.SampleRate);
            TimeSpan chunkEnd = SamplesToTime(chunk.StartSample + chunk.Samples.Length, reader.SampleRate);

            await foreach (SegmentData data in processor.ProcessAsync(chunk.Samples, cancellationToken))
            {
                string text = data.Text.Trim();
                if (IsNonSpeech(text))
                {
                    continue;
                }

                // Only trust the detected language once there is real speech; silence or music detects poorly.
                language ??= string.IsNullOrEmpty(data.Language) ? null : data.Language;

                TimeSpan start = Clamp(offset + data.Start, offset, chunkEnd);
                TimeSpan end = Clamp(offset + data.End, start, chunkEnd);
                var segment = new TranscriptSegment(start, end, text);
                segments.Add(segment);
                onSegment?.Invoke(segment);
            }

            // Detect the language once (on the first chunk with speech) and keep it for the rest of the file, so a
            // long video doesn't switch languages halfway through.
            if (!languageLocked && language is not null)
            {
                processor.ChangeLanguage(language);
                languageLocked = true;
            }

            onProgress?.Invoke((chunk.StartSample + chunk.Samples.Length) / (double)totalSamples);
        }

        return new TranscriptionResult(segments, language);
    }

    public void Dispose() => factory.Dispose();

    private static TimeSpan SamplesToTime(long samples, int sampleRate) =>
        TimeSpan.FromTicks((long)Math.Round(samples * (double)TimeSpan.TicksPerSecond / sampleRate));

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>whisper.cpp emits markers such as "[BLANK_AUDIO]" for silence; they aren't part of the transcript.</summary>
    private static bool IsNonSpeech(string text) =>
        text.Length == 0 || text.Equals("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates the factory and forces the model to load, so failures surface here.</summary>
    private static WhisperFactory CreateFactory(string modelPath, bool useGpu)
    {
        WhisperFactory factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
        try
        {
            // Building a processor loads the model now rather than on first use.
            using WhisperProcessor probe = factory.CreateBuilder().Build();
            return factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private static UserFacingException ModelLoadFailure(string modelPath, Exception ex) =>
        // The file already passed the format check, so don't delete it: the usual cause is too little memory.
        new(
            $"The Whisper model couldn't be loaded: {modelPath}",
            "Your PC may not have enough free memory for this model. Close other programs or try a smaller --model. "
            + "If the problem persists, the file may be damaged: delete it and run VideoTranscriber again to download a fresh copy.",
            ex);

    private static bool IsNativeLoadFailure(Exception ex) =>
        ex is DllNotFoundException or BadImageFormatException or PlatformNotSupportedException or FileNotFoundException
        || ex.Message.Contains("native whisper library", StringComparison.OrdinalIgnoreCase)
        || ex.InnerException is { } inner && IsNativeLoadFailure(inner);

    private static UserFacingException NativeLoadFailure(Exception ex)
    {
        string hint = OperatingSystem.IsWindows()
            ? "Install the Microsoft Visual C++ Redistributable (x64) from https://aka.ms/vs/17/release/vc_redist.x64.exe and try again. "
              + "The speech engine officially supports 64-bit Windows 11 and Windows Server 2022 or newer. Run with --cpu to rule out GPU driver problems."
            : "Make sure libstdc++ and glibc 2.31+ are installed. Run with --cpu to rule out GPU driver problems.";
        return new UserFacingException($"Couldn't load the speech recognition engine: {ex.Message}", hint, ex);
    }

    /// <summary>
    /// Works out from whisper.cpp's log output whether the model really ended up on a GPU. A GPU runtime can load
    /// successfully yet find no usable device, in which case whisper.cpp silently runs on the CPU.
    /// </summary>
    private sealed partial class GpuDeviceDetector
    {
        private string? deviceName;

        /// <summary>Buffer that holds the model weights, e.g. "CPU", "Vulkan0" or "CUDA0".</summary>
        public string? ModelBuffer { get; private set; }

        public bool ModelOnGpu => ModelBuffer is not null && !ModelBuffer.StartsWith("CPU", StringComparison.OrdinalIgnoreCase);

        /// <summary>Friendly GPU name when the backend logged one, otherwise the buffer name.</summary>
        public string? Device => ModelOnGpu ? deviceName ?? ModelBuffer : null;

        public void Observe(string message)
        {
            Match buffer = ModelBufferPattern().Match(message);
            if (buffer.Success && (ModelBuffer is null || ModelBuffer.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)))
            {
                ModelBuffer = buffer.Groups[1].Value;
            }

            Match device = DeviceNamePattern().Match(message);
            if (device.Success && deviceName is null)
            {
                deviceName = device.Groups["name"].Value.Trim();
            }
        }

        // "whisper_model_load:      Vulkan0 total size =    77.11 MB"
        [GeneratedRegex(@"whisper_model_load:\s+(\S+) total size")]
        private static partial Regex ModelBufferPattern();

        // Vulkan: "ggml_vulkan: 0 = NVIDIA GeForce RTX 3060 (NVIDIA) | uma: 0 ..."; CUDA: "Device 0: NVIDIA GeForce RTX 3060, compute capability 8.6"
        [GeneratedRegex(@"ggml_vulkan: 0 = (?<name>[^|(]+)|Device 0: (?<name>[^,]+),")]
        private static partial Regex DeviceNamePattern();
    }
}
