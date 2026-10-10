using System.Text.Json;
using VideoTranscriber.Infrastructure;

namespace VideoTranscriber.Gui.Services;

/// <summary>The choices the user made last time, restored on the next start.</summary>
public sealed class AppSettings
{
    public string Model { get; set; } = "base";
    public string Language { get; set; } = "auto";
    public bool Translate { get; set; }
    public bool Txt { get; set; } = true;
    public bool Srt { get; set; } = true;
    public bool Vtt { get; set; }
    public bool Json { get; set; }
    public bool SaveNextToVideo { get; set; } = true;
    public string? OutputFolder { get; set; }
    public bool UseGpu { get; set; } = true;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. A null path keeps settings in memory only.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string? path;

    public SettingsStore(string? path)
    {
        this.path = path;
    }

    public static SettingsStore Default { get; } = new(Path.Combine(AppPaths.DataDirectory, "settings.json"));

    public AppSettings Load()
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or unreadable settings file just means starting with defaults.
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember settings isn't worth interrupting the user for.
        }
    }
}
