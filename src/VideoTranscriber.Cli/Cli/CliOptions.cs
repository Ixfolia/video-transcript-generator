using VideoTranscriber.Models;
using VideoTranscriber.Output;

namespace VideoTranscriber.Cli;

/// <summary>Validated command-line settings for one run.</summary>
public sealed record CliOptions(
    IReadOnlyList<string> InputFiles,
    WhisperModelInfo Model,
    string Language,
    string? OutputDirectory,
    bool Translate,
    IReadOnlyList<OutputFormat> Formats,
    bool CpuOnly,
    bool Verbose);
