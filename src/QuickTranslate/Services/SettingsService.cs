using System.IO;
using System.Text.Json;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickTranslate");

    public static string FilePath { get; } = Path.Combine(Dir, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null)
                {
                    if (s.Hotkeys == null || s.Hotkeys.Count == 0) s.Hotkeys = AppSettings.DefaultHotkeys();
                    s.Api ??= new ApiSettings();
                    s.General ??= new GeneralSettings();
                    s.General.MigrateLegacy();
                    s.Api.KnownModels ??= new List<string>();
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Error(ex); }
        return new AppSettings();
    }

    public void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch (Exception ex) { Log.Error(ex); }
    }
}
