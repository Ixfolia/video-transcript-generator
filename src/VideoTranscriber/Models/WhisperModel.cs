namespace VideoTranscriber.Models;

public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,
}

/// <summary>Static facts about each supported Whisper GGML model.</summary>
public sealed record WhisperModelInfo(WhisperModelSize Size, string Name, string FileName, long ApproximateBytes)
{
    public static readonly IReadOnlyList<WhisperModelInfo> All =
    [
        new(WhisperModelSize.Tiny, "tiny", "ggml-tiny.bin", 77_691_713),
        new(WhisperModelSize.Base, "base", "ggml-base.bin", 147_951_465),
        new(WhisperModelSize.Small, "small", "ggml-small.bin", 487_601_967),
        new(WhisperModelSize.Medium, "medium", "ggml-medium.bin", 1_533_763_059),
    ];

    public static IEnumerable<string> Names => All.Select(m => m.Name);

    public static WhisperModelInfo Get(WhisperModelSize size) => All.Single(m => m.Size == size);

    public static WhisperModelInfo Parse(string name) =>
        All.SingleOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown model '{name}'. Valid models: {string.Join(", ", Names)}.", nameof(name));

    /// <summary>Download locations, tried in order.</summary>
    public IReadOnlyList<Uri> DownloadUrls =>
    [
        new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}"),
        new($"https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/{FileName}"),
    ];
}
