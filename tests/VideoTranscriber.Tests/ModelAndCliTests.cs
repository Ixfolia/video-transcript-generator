using System.CommandLine;
using VideoTranscriber.Cli;
using VideoTranscriber.Models;
using VideoTranscriber.Output;

namespace VideoTranscriber.Tests;

public sealed class ModelManagerTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "vt-model-" + Guid.NewGuid().ToString("N") + ".bin");

    public void Dispose() => File.Delete(file);

    [Fact]
    public void AcceptsFileWithGgmlMagic()
    {
        byte[] content = new byte[2 * 1024 * 1024];
        "lmgg"u8.CopyTo(content);
        File.WriteAllBytes(file, content);

        Assert.True(ModelManager.IsValidModelFile(file));
    }

    [Fact]
    public void RejectsMissingTruncatedOrNonModelFiles()
    {
        Assert.False(ModelManager.IsValidModelFile(file));

        File.WriteAllBytes(file, "lmgg"u8.ToArray());
        Assert.False(ModelManager.IsValidModelFile(file));

        File.WriteAllBytes(file, new byte[2 * 1024 * 1024]); // e.g. an HTML error page or zeros
        Assert.False(ModelManager.IsValidModelFile(file));
    }

    [Theory]
    [InlineData("tiny", "ggml-tiny.bin")]
    [InlineData("BASE", "ggml-base.bin")]
    [InlineData("small", "ggml-small.bin")]
    [InlineData("medium", "ggml-medium.bin")]
    public void ModelNamesMapToGgmlFiles(string name, string fileName)
    {
        WhisperModelInfo model = WhisperModelInfo.Parse(name);

        Assert.Equal(fileName, model.FileName);
        Assert.All(model.DownloadUrls, url => Assert.EndsWith("/" + fileName, url.AbsoluteUri));
    }
}

public class CommandLineTests
{
    private static (int ExitCode, CliOptions? Options) Run(params string[] args)
    {
        CliOptions? captured = null;
        RootCommand root = CommandLineDefinition.Create((options, _) =>
        {
            captured = options;
            return Task.FromResult(0);
        });

        ParseResult result = root.Parse(args);
        if (result.Errors.Count > 0)
        {
            return (2, null);
        }

        return (result.Invoke(new InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null }), captured);
    }

    [Fact]
    public void Defaults()
    {
        (int exitCode, CliOptions? options) = Run("talk.mp4");

        Assert.Equal(0, exitCode);
        Assert.NotNull(options);
        Assert.Equal(["talk.mp4"], options.InputFiles);
        Assert.Equal(WhisperModelSize.Base, options.Model.Size);
        Assert.Equal("auto", options.Language);
        Assert.Null(options.OutputDirectory);
        Assert.False(options.Translate);
        Assert.Equal([OutputFormat.Txt, OutputFormat.Srt], options.Formats);
        Assert.False(options.CpuOnly);
    }

    [Fact]
    public void AllOptions()
    {
        (_, CliOptions? options) = Run("a.mp4", "b.mkv", "--model", "small", "--language", "DE", "--format", "vtt,json",
            "--output", "out", "--translate", "--cpu");

        Assert.NotNull(options);
        Assert.Equal(["a.mp4", "b.mkv"], options.InputFiles);
        Assert.Equal(WhisperModelSize.Small, options.Model.Size);
        Assert.Equal("de", options.Language);
        Assert.Equal([OutputFormat.Vtt, OutputFormat.Json], options.Formats);
        Assert.Equal("out", options.OutputDirectory);
        Assert.True(options.Translate);
        Assert.True(options.CpuOnly);
    }

    [Fact]
    public void ShortAliases()
    {
        (_, CliOptions? options) = Run("a.mp4", "-m", "tiny", "-l", "en", "-f", "all", "-o", "x", "-t");

        Assert.NotNull(options);
        Assert.Equal(WhisperModelSize.Tiny, options.Model.Size);
        Assert.Equal(4, options.Formats.Count);
        Assert.True(options.Translate);
    }

    [Theory]
    [InlineData("a.mp4", "--model", "large")]
    [InlineData("a.mp4", "--format", "docx")]
    [InlineData("a.mp4", "--language", "english")]
    [InlineData("a.mp4", "--unknown")]
    public void InvalidArgumentsAreRejected(params string[] args)
    {
        Assert.Equal(2, Run(args).ExitCode);
    }

    [Fact]
    public void VideoArgumentIsRequired()
    {
        Assert.Equal(2, Run("--model", "tiny").ExitCode);
    }
}
