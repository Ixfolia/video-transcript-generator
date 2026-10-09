using VideoTranscriber.Output;

namespace VideoTranscriber.Tests;

public sealed class TranscriptOutputServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vt-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(OutputFormat.Txt, "my talk.txt")]
    [InlineData(OutputFormat.Srt, "my talk.srt")]
    [InlineData(OutputFormat.Vtt, "my talk.vtt")]
    [InlineData(OutputFormat.Json, "my talk.json")]
    public void OutputFileIsNamedAfterTheVideo(OutputFormat format, string expectedName)
    {
        string path = TranscriptOutputService.GetOutputPath(Path.Combine("in", "my talk.mp4"), "out", format);

        Assert.Equal(Path.Combine("out", expectedName), path);
    }

    [Fact]
    public void WriteAll_CreatesFolderAndWritesUtf8WithoutBom()
    {
        var transcript = new Transcript(
            SourceFile: Path.Combine("anywhere", "clip.mkv"),
            Model: "tiny",
            Language: "de",
            Translated: false,
            Duration: TimeSpan.FromSeconds(2),
            Segments: [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Schöne Grüße")]);

        IReadOnlyList<string> written = new TranscriptOutputService()
            .WriteAll(transcript, directory, [OutputFormat.Txt, OutputFormat.Srt, OutputFormat.Vtt, OutputFormat.Json]);

        Assert.Equal(
            new[] { "clip.txt", "clip.srt", "clip.vtt", "clip.json" },
            written.Select(Path.GetFileName));
        Assert.All(written, path => Assert.True(File.Exists(path)));

        byte[] bytes = File.ReadAllBytes(Path.Combine(directory, "clip.txt"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "File should not start with a BOM.");
        Assert.Equal("Schöne Grüße\n", File.ReadAllText(Path.Combine(directory, "clip.txt")));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void WriteAll_OverwritesExistingFiles()
    {
        Directory.CreateDirectory(directory);
        string existing = Path.Combine(directory, "clip.txt");
        File.WriteAllText(existing, "old content that is longer than the new one");

        var transcript = new Transcript("clip.mp4", "tiny", "en", false, TimeSpan.FromSeconds(1),
            [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "new")]);
        new TranscriptOutputService().WriteAll(transcript, directory, [OutputFormat.Txt]);

        Assert.Equal("new\n", File.ReadAllText(existing));
    }
}
