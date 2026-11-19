using System.Text.Json;
using System.Text.Json.Serialization;
using Frost.Core;

namespace Frost.App;

public enum ViewMode { Details, Tiles, Large, ExtraLarge }

public sealed class AppSettings
{
    public List<string> Pinned { get; set; } = new();
    public ViewMode View { get; set; } = ViewMode.Details;
    public double IconSize { get; set; } = 64;
    public SortColumn SortColumn { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }
    public bool ShowHidden { get; set; }
    public bool PreviewPane { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool Maximized { get; set; }
    public List<string> OpenTabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public List<string> CollapsedSections { get; set; } = new();
    public bool AskedDefault { get; set; }
    public DateTime LastUpdateCheck { get; set; } // UTC
    public string? SkippedVersion { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext { }

public static class SettingsStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Frost", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
