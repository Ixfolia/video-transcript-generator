using VideoTranscriber.Output;

namespace VideoTranscriber.Tests;

public class OutputFormatParserTests
{
    [Theory]
    [InlineData("txt,srt", new[] { OutputFormat.Txt, OutputFormat.Srt })]
    [InlineData("SRT, vtt", new[] { OutputFormat.Srt, OutputFormat.Vtt })]
    [InlineData(".json", new[] { OutputFormat.Json })]
    [InlineData("srt,srt,txt", new[] { OutputFormat.Srt, OutputFormat.Txt })]
    [InlineData("all", new[] { OutputFormat.Txt, OutputFormat.Srt, OutputFormat.Vtt, OutputFormat.Json })]
    public void ParsesCommaSeparatedFormats(string input, OutputFormat[] expected)
    {
        Assert.True(OutputFormatParser.TryParse([input], out IReadOnlyList<OutputFormat> formats, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, formats);
    }

    [Theory]
    [InlineData("docx")]
    [InlineData("txt,pdf")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData(" , ")]
    public void RejectsUnknownOrEmptyFormats(string input)
    {
        Assert.False(OutputFormatParser.TryParse([input], out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void DefaultIsTxtAndSrt()
    {
        Assert.Equal(new[] { OutputFormat.Txt, OutputFormat.Srt }, OutputFormatParser.Default);
    }
}
