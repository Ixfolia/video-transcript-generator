namespace VideoTranscriber.Output;

/// <summary>Chooses output file names so that files from one run never overwrite each other.</summary>
public static class OutputNaming
{
    /// <summary>
    /// Returns the base name (without extension) for each input. Normally this is the video's name, e.g.
    /// <c>talk</c> for <c>talk.mp4</c>. When two inputs would write to the same folder under the same name
    /// (<c>talk.mp4</c> and <c>talk.mkv</c>, or two <c>talk.mp4</c> files from different folders with
    /// <c>--output</c>), the video's extension is kept (<c>talk.mp4</c>, <c>talk.mkv</c>) and, if that still clashes,
    /// a number is added (<c>talk.mp4 (2)</c>).
    /// </summary>
    public static IReadOnlyList<string> AssignBaseNames(IReadOnlyList<(string InputPath, string OutputDirectory)> files)
    {
        // Windows and macOS file systems are case-insensitive, so compare that way everywhere to be safe.
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        string Key(string directory, string name) => Path.Combine(Path.GetFullPath(directory), name);

        string[] preferred = files.Select(f => Path.GetFileNameWithoutExtension(f.InputPath)).ToArray();
        var preferredCounts = files
            .Select((f, i) => Key(f.OutputDirectory, preferred[i]))
            .GroupBy(k => k, comparer)
            .ToDictionary(g => g.Key, g => g.Count(), comparer);

        var used = new HashSet<string>(comparer);
        var result = new string[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            string directory = files[i].OutputDirectory;
            string name = preferredCounts[Key(directory, preferred[i])] > 1 ? Path.GetFileName(files[i].InputPath) : preferred[i];

            string candidate = name;
            for (int n = 2; !used.Add(Key(directory, candidate)); n++)
            {
                candidate = $"{name} ({n})";
            }

            result[i] = candidate;
        }

        return result;
    }
}
