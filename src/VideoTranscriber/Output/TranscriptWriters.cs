using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoTranscriber.Output;

/// <summary>Writes a transcript in one output format.</summary>
public interface ITranscriptWriter
{
    OutputFormat Format { get; }

    /// <summary>File extension including the dot, e.g. <c>.srt</c>.</summary>
    string Extension { get; }

    void Write(Transcript transcript, TextWriter writer);
}

/// <summary>Plain text: one segment per line, no timestamps.</summary>
public sealed class TextTranscriptWriter : ITranscriptWriter
{
    public OutputFormat Format => OutputFormat.Txt;
    public string Extension => ".txt";

    public void Write(Transcript transcript, TextWriter writer)
    {
        foreach (TranscriptSegment segment in transcript.Segments)
        {
            string text = CueText.SingleLine(segment.Text);
            if (text.Length > 0)
            {
                writer.Write(text);
                writer.Write('\n');
            }
        }
    }
}

/// <summary>SubRip (.srt) subtitles.</summary>
public sealed class SrtWriter : ITranscriptWriter
{
    public OutputFormat Format => OutputFormat.Srt;
    public string Extension => ".srt";

    public void Write(Transcript transcript, TextWriter writer)
    {
        int index = 1;
        foreach (TranscriptSegment segment in transcript.Segments)
        {
            string text = CueText.MultiLine(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }

            if (index > 1)
            {
                writer.Write('\n');
            }

            writer.Write(index.ToString(CultureInfo.InvariantCulture));
            writer.Write('\n');
            writer.Write($"{TimestampFormatter.ToSrt(segment.Start)} --> {TimestampFormatter.ToSrt(CueText.EndAfterStart(segment))}\n");
            writer.Write(text);
            writer.Write('\n');
            index++;
        }
    }
}

/// <summary>WebVTT (.vtt) subtitles.</summary>
public sealed class VttWriter : ITranscriptWriter
{
    public OutputFormat Format => OutputFormat.Vtt;
    public string Extension => ".vtt";

    public void Write(Transcript transcript, TextWriter writer)
    {
        writer.Write("WEBVTT\n");
        foreach (TranscriptSegment segment in transcript.Segments)
        {
            string text = EscapeCueText(CueText.MultiLine(segment.Text));
            if (text.Length == 0)
            {
                continue;
            }

            writer.Write('\n');
            writer.Write($"{TimestampFormatter.ToVtt(segment.Start)} --> {TimestampFormatter.ToVtt(CueText.EndAfterStart(segment))}\n");
            writer.Write(text);
            writer.Write('\n');
        }
    }

    /// <summary>
    /// In WebVTT cue text "&amp;" starts an escape and "&lt;" starts a tag, so both must be escaped (and "&gt;" too,
    /// which also keeps the "-->" timing separator out of the text).
    /// </summary>
    internal static string EscapeCueText(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>JSON with metadata and timed segments.</summary>
public sealed class JsonTranscriptWriter : ITranscriptWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep accented and non-Latin text readable instead of \uXXXX escapes (still valid JSON).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public OutputFormat Format => OutputFormat.Json;
    public string Extension => ".json";

    public void Write(Transcript transcript, TextWriter writer)
    {
        var document = new JsonTranscript(
            Source: Path.GetFileName(transcript.SourceFile),
            Model: transcript.Model,
            Language: transcript.Language,
            Translated: transcript.Translated,
            Duration: TimestampFormatter.ToSeconds(transcript.Duration),
            Segments: transcript.Segments
                .Select(s => (Segment: s, Text: CueText.SingleLine(s.Text)))
                .Where(x => x.Text.Length > 0)
                .Select((x, i) => new JsonSegment(
                    Id: i + 1,
                    Start: TimestampFormatter.ToSeconds(x.Segment.Start),
                    End: TimestampFormatter.ToSeconds(CueText.EndAfterStart(x.Segment)),
                    StartTime: TimestampFormatter.ToVtt(x.Segment.Start),
                    EndTime: TimestampFormatter.ToVtt(CueText.EndAfterStart(x.Segment)),
                    Text: x.Text))
                .ToList());

        writer.Write(JsonSerializer.Serialize(document, SerializerOptions).ReplaceLineEndings("\n"));
        writer.Write('\n');
    }

    private sealed record JsonTranscript(
        string Source,
        string Model,
        string? Language,
        bool Translated,
        double Duration,
        IReadOnlyList<JsonSegment> Segments);

    private sealed record JsonSegment(int Id, double Start, double End, string StartTime, string EndTime, string Text);
}

/// <summary>Text clean-up shared by the writers.</summary>
internal static class CueText
{
    /// <summary>Collapses all whitespace (including line breaks) into single spaces.</summary>
    public static string SingleLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Keeps intentional line breaks but removes blank lines, which would end a subtitle cue early.</summary>
    public static string MultiLine(string text) =>
        string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(SingleLine).Where(l => l.Length > 0));

    /// <summary>Subtitle players reject cues whose end is before their start.</summary>
    public static TimeSpan EndAfterStart(TranscriptSegment segment) =>
        segment.End >= segment.Start ? segment.End : segment.Start;
}
