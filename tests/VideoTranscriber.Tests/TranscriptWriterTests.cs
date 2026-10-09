using System.Text.Json;
using VideoTranscriber.Output;

namespace VideoTranscriber.Tests;

public class TranscriptWriterTests
{
    private static Transcript CreateTranscript(params TranscriptSegment[] segments) =>
        new(
            SourceFile: Path.Combine("videos", "talk.mp4"),
            Model: "base",
            Language: "en",
            Translated: false,
            Duration: TimeSpan.FromSeconds(10),
            Segments: segments);

    private static readonly Transcript Sample = CreateTranscript(
        new TranscriptSegment(TimeSpan.FromMilliseconds(0), TimeSpan.FromMilliseconds(1_500), "Hello there."),
        new TranscriptSegment(TimeSpan.FromMilliseconds(1_500), TimeSpan.FromMilliseconds(4_250), " General Kenobi! "));

    private static string Render(ITranscriptWriter writer, Transcript transcript)
    {
        using var text = new StringWriter();
        writer.Write(transcript, text);
        return text.ToString();
    }

    [Fact]
    public void Txt_WritesOneTrimmedLinePerSegment()
    {
        Assert.Equal("Hello there.\nGeneral Kenobi!\n", Render(new TextTranscriptWriter(), Sample));
    }

    [Fact]
    public void Srt_WritesNumberedCuesWithCommaTimestamps()
    {
        const string expected =
            "1\n" +
            "00:00:00,000 --> 00:00:01,500\n" +
            "Hello there.\n" +
            "\n" +
            "2\n" +
            "00:00:01,500 --> 00:00:04,250\n" +
            "General Kenobi!\n";

        Assert.Equal(expected, Render(new SrtWriter(), Sample));
    }

    [Fact]
    public void Vtt_WritesHeaderAndDotTimestamps()
    {
        const string expected =
            "WEBVTT\n" +
            "\n" +
            "00:00:00.000 --> 00:00:01.500\n" +
            "Hello there.\n" +
            "\n" +
            "00:00:01.500 --> 00:00:04.250\n" +
            "General Kenobi!\n";

        Assert.Equal(expected, Render(new VttWriter(), Sample));
    }

    [Fact]
    public void Srt_SkipsEmptySegmentsAndKeepsNumberingContiguous()
    {
        Transcript transcript = CreateTranscript(
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "One"),
            new TranscriptSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "   "),
            new TranscriptSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "Two"));

        string srt = Render(new SrtWriter(), transcript);

        Assert.Contains("1\n00:00:00,000 --> 00:00:01,000\nOne\n", srt);
        Assert.Contains("2\n00:00:02,000 --> 00:00:03,000\nTwo\n", srt);
        Assert.DoesNotContain("3\n", srt);
    }

    [Fact]
    public void SubtitleCuesNeverContainBlankLines()
    {
        Transcript transcript = CreateTranscript(
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "First line\r\n\r\nSecond line"));

        Assert.Contains("First line\nSecond line\n", Render(new SrtWriter(), transcript));
        Assert.Contains("First line\nSecond line\n", Render(new VttWriter(), transcript));
    }

    [Fact]
    public void Vtt_EscapesCueTimingArrowInText()
    {
        Transcript transcript = CreateTranscript(
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "a --> b"));

        string vtt = Render(new VttWriter(), transcript);

        Assert.Contains("\na -> b\n", vtt);
        Assert.Single(vtt.Split('\n'), line => line.Contains("-->", StringComparison.Ordinal));
    }

    [Fact]
    public void EndTimeBeforeStartIsClampedToStart()
    {
        Transcript transcript = CreateTranscript(
            new TranscriptSegment(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), "Backwards"));

        Assert.Contains("00:00:05,000 --> 00:00:05,000", Render(new SrtWriter(), transcript));
    }

    [Fact]
    public void Json_ContainsMetadataAndSegmentsWithSecondsAndTimestamps()
    {
        string json = Render(new JsonTranscriptWriter(), Sample);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("talk.mp4", root.GetProperty("source").GetString());
        Assert.Equal("base", root.GetProperty("model").GetString());
        Assert.Equal("en", root.GetProperty("language").GetString());
        Assert.False(root.GetProperty("translated").GetBoolean());
        Assert.Equal(10.0, root.GetProperty("duration").GetDouble());

        JsonElement[] segments = root.GetProperty("segments").EnumerateArray().ToArray();
        Assert.Equal(2, segments.Length);
        Assert.Equal(1, segments[0].GetProperty("id").GetInt32());
        Assert.Equal(0.0, segments[0].GetProperty("start").GetDouble());
        Assert.Equal(1.5, segments[0].GetProperty("end").GetDouble());
        Assert.Equal("00:00:01.500", segments[1].GetProperty("startTime").GetString());
        Assert.Equal("00:00:04.250", segments[1].GetProperty("endTime").GetString());
        Assert.Equal("General Kenobi!", segments[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Json_KeepsNonAsciiTextReadable()
    {
        Transcript transcript = CreateTranscript(
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Grüße aus München – 東京"));

        string json = Render(new JsonTranscriptWriter(), transcript);

        Assert.Contains("Grüße aus München – 東京", json);
        Assert.Equal("Grüße aus München – 東京",
            JsonDocument.Parse(json).RootElement.GetProperty("segments")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void EmptyTranscriptProducesValidFiles()
    {
        Transcript empty = CreateTranscript();

        Assert.Equal(string.Empty, Render(new TextTranscriptWriter(), empty));
        Assert.Equal(string.Empty, Render(new SrtWriter(), empty));
        Assert.Equal("WEBVTT\n", Render(new VttWriter(), empty));
        Assert.Empty(JsonDocument.Parse(Render(new JsonTranscriptWriter(), empty)).RootElement.GetProperty("segments").EnumerateArray());
    }
}
