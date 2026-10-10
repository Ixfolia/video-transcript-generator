namespace VideoTranscriber.Output;

/// <summary>One timed piece of the transcript.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>The full transcription result passed to the writers.</summary>
public sealed record Transcript(
    string SourceFile,
    string Model,
    string? Language,
    bool Translated,
    TimeSpan Duration,
    IReadOnlyList<TranscriptSegment> Segments);
