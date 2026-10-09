using System.Text;

namespace VideoTranscriber.Output;

/// <summary>Picks writers for the requested formats and saves the files.</summary>
public sealed class TranscriptOutputService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly IReadOnlyDictionary<OutputFormat, ITranscriptWriter> Writers =
        new ITranscriptWriter[] { new TextTranscriptWriter(), new SrtWriter(), new VttWriter(), new JsonTranscriptWriter() }
            .ToDictionary(w => w.Format);

    public static ITranscriptWriter GetWriter(OutputFormat format) => Writers[format];

    /// <summary>Returns the output path for <paramref name="format"/>, e.g. <c>C:\videos\talk.srt</c>.</summary>
    public static string GetOutputPath(string inputFile, string outputDirectory, OutputFormat format) =>
        Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputFile) + GetWriter(format).Extension);

    /// <summary>Writes every requested format and returns the created file paths.</summary>
    public IReadOnlyList<string> WriteAll(Transcript transcript, string outputDirectory, IEnumerable<OutputFormat> formats)
    {
        Directory.CreateDirectory(outputDirectory);

        var written = new List<string>();
        foreach (OutputFormat format in formats)
        {
            string path = GetOutputPath(transcript.SourceFile, outputDirectory, format);

            // Write to a temporary file first so an interrupted run never leaves a half-written transcript behind.
            string tempPath = path + ".tmp";
            try
            {
                using (var writer = new StreamWriter(tempPath, append: false, Utf8NoBom))
                {
                    GetWriter(format).Write(transcript, writer);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            written.Add(path);
        }

        return written;
    }
}
