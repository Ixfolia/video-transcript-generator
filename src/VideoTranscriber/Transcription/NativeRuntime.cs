using System.Runtime.InteropServices;
using Whisper.net.LibraryLoader;

namespace VideoTranscriber.Transcription;

/// <summary>Chooses and prepares the native whisper.cpp runtime (CUDA, Vulkan or CPU).</summary>
internal static class NativeRuntime
{
    private static readonly string[] VisualCppRuntimeDlls = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];

    /// <summary>Must run before the first <see cref="Whisper.net.WhisperFactory"/> is created.</summary>
    public static void Configure(bool cpuOnly)
    {
        // Whisper.net tries CUDA, then Vulkan, then CPU (AVX2), then CPU without AVX, skipping any runtime whose
        // libraries aren't bundled or fail to load (e.g. no NVIDIA driver, no Vulkan driver).
        RuntimeOptions.RuntimeLibraryOrder = cpuOnly
            ? [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        if (OperatingSystem.IsWindows())
        {
            PreloadVisualCppRuntime();
        }
        else if (OperatingSystem.IsLinux())
        {
            UseFixedMmapThreshold();
        }
    }

    public static string Describe(RuntimeLibrary? library) => library switch
    {
        RuntimeLibrary.Cuda => "NVIDIA CUDA 13 (GPU)",
        RuntimeLibrary.Cuda12 => "NVIDIA CUDA 12 (GPU)",
        RuntimeLibrary.Vulkan => "Vulkan (GPU)",
        RuntimeLibrary.Cpu => "CPU",
        RuntimeLibrary.CpuNoAvx => "CPU (compatibility mode, no AVX)",
        null => "unknown",
        _ => library.ToString()!,
    };

    /// <summary>
    /// whisper.cpp allocates and frees large buffers for every audio chunk. glibc raises its mmap threshold after such
    /// frees, so later large buffers come from the heap and the memory is never returned: process memory grew by
    /// ~80 MB per 10-minute chunk in testing. A fixed threshold keeps big buffers in mmap, so memory stays flat.
    /// </summary>
    private static void UseFixedMmapThreshold()
    {
        const int M_MMAP_THRESHOLD = -3;
        try
        {
            mallopt(M_MMAP_THRESHOLD, 128 * 1024);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not glibc (e.g. musl); nothing to tune.
        }
    }

    [DllImport("libc", EntryPoint = "mallopt")]
    private static extern int mallopt(int parameter, int value);

    /// <summary>
    /// The native whisper libraries need the Microsoft Visual C++ runtime. Most PCs have it installed; if not, load
    /// the copies bundled in the app's vcredist folder (added by the release build) so the user doesn't have to
    /// install anything. Once loaded, Windows reuses them for the whisper DLLs.
    /// </summary>
    private static void PreloadVisualCppRuntime()
    {
        if (VisualCppRuntimeDlls.All(dll => NativeLibrary.TryLoad(dll, out _)))
        {
            return;
        }

        string bundledFolder = Path.Combine(AppContext.BaseDirectory, "vcredist");
        foreach (string dll in VisualCppRuntimeDlls)
        {
            string path = Path.Combine(bundledFolder, dll);
            if (File.Exists(path))
            {
                NativeLibrary.TryLoad(path, out _);
            }
        }
    }
}
