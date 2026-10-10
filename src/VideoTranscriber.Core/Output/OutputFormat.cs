namespace VideoTranscriber.Output;

public enum OutputFormat
{
    Txt,
    Srt,
    Vtt,
    Json,
}

public static class OutputFormatParser
{
    public static readonly IReadOnlyList<OutputFormat> Default = [OutputFormat.Txt, OutputFormat.Srt];

    private static readonly char[] FormatSeparators = [',', ';', ' '];

    public static string ValidNames => "txt, srt, vtt, json (or all)";

    /// <summary>
    /// Parses values such as <c>"txt,srt"</c>, <c>"srt vtt"</c> or <c>"all"</c>. Each input string may itself be a
    /// comma-separated list. Duplicates are removed; order follows first appearance.
    /// </summary>
    public static bool TryParse(IEnumerable<string> values, out IReadOnlyList<OutputFormat> formats, out string? error)
    {
        var result = new List<OutputFormat>();
        foreach (string raw in values.SelectMany(v => v.Split(FormatSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            string name = raw.TrimStart('.').ToLowerInvariant();
            if (name == "all")
            {
                result.AddRange(Enum.GetValues<OutputFormat>());
                continue;
            }

            if (!Enum.TryParse(name, ignoreCase: true, out OutputFormat format) || !Enum.IsDefined(format) || int.TryParse(name, out _))
            {
                formats = [];
                error = $"Unknown output format '{raw}'. Valid formats: {ValidNames}.";
                return false;
            }

            result.Add(format);
        }

        if (result.Count == 0)
        {
            formats = [];
            error = $"No output format given. Valid formats: {ValidNames}.";
            return false;
        }

        formats = result.Distinct().ToList();
        error = null;
        return true;
    }
}
