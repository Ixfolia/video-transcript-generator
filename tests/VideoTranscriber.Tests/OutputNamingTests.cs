using VideoTranscriber.Output;

namespace VideoTranscriber.Tests;

public class OutputNamingTests
{
    private static readonly string Videos = Path.Combine(Path.GetTempPath(), "videos");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "other");
    private static readonly string Out = Path.Combine(Path.GetTempPath(), "out");

    [Fact]
    public void UsesTheVideoNameWhenThereIsNoClash()
    {
        IReadOnlyList<string> names = OutputNaming.AssignBaseNames(
        [
            (Path.Combine(Videos, "talk.mp4"), Videos),
            (Path.Combine(Videos, "intro.mkv"), Videos),
        ]);

        Assert.Equal(["talk", "intro"], names);
    }

    [Fact]
    public void SameNameDifferentExtensionKeepsTheExtension()
    {
        IReadOnlyList<string> names = OutputNaming.AssignBaseNames(
        [
            (Path.Combine(Videos, "talk.mp4"), Videos),
            (Path.Combine(Videos, "talk.mkv"), Videos),
            (Path.Combine(Videos, "intro.mkv"), Videos),
        ]);

        Assert.Equal(["talk.mp4", "talk.mkv", "intro"], names);
    }

    [Fact]
    public void IdenticalNamesFromDifferentFoldersIntoOneOutputFolderGetNumbered()
    {
        IReadOnlyList<string> names = OutputNaming.AssignBaseNames(
        [
            (Path.Combine(Videos, "talk.mp4"), Out),
            (Path.Combine(Other, "talk.mp4"), Out),
        ]);

        Assert.Equal(["talk.mp4", "talk.mp4 (2)"], names);
    }

    [Fact]
    public void SameNameInDifferentOutputFoldersIsNotAClash()
    {
        IReadOnlyList<string> names = OutputNaming.AssignBaseNames(
        [
            (Path.Combine(Videos, "talk.mp4"), Videos),
            (Path.Combine(Other, "talk.mp4"), Other),
        ]);

        Assert.Equal(["talk", "talk"], names);
    }

    [Fact]
    public void ClashDetectionIgnoresCase()
    {
        IReadOnlyList<string> names = OutputNaming.AssignBaseNames(
        [
            (Path.Combine(Videos, "Talk.mp4"), Videos),
            (Path.Combine(Videos, "talk.MOV"), Videos),
        ]);

        Assert.Equal(["Talk.mp4", "talk.MOV"], names);
    }
}
