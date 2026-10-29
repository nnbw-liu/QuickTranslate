using System.IO;
using System.Text.Json;
using SDemo.QuickTranslate.Models;

namespace SDemo.QuickTranslate.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDemo", "QuickTranslate");

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

                    s.DefaultModel ??= new ModelRef();
                    s.Providers ??= new List<ApiProvider>();
                    foreach (var h in s.Hotkeys) h.Model ??= new ModelRef();
                    s.General.MigrateLegacy();
                    s.Api.KnownModels ??= new List<string>();
                    if (string.IsNullOrWhiteSpace(s.General.LastInputTargetLanguage))
                        s.General.LastInputTargetLanguage = "中文";
                    MigrateProviders(s);
                    MigrateInputTranslateHotkey(s);
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Error(ex); }
        var fresh = new AppSettings();
        MigrateProviders(fresh);
        return fresh;
    }

    public void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            SyncLegacyApi(s);   // 旧版本 / --selftest 仍能从 api 段读到「当前默认的」地址、Key 与模型
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>
    /// 把旧的单 API 配置升级成「供应商列表 + 默认模型」：
    /// 旧版只有一个地址/Key/模型，迁移后就是唯一一个供应商，且默认模型指向它（行为与升级前完全一致）。
    /// 有 providers 的配置再打开时不动它，只补默认模型。
    /// </summary>
    private void MigrateProviders(AppSettings s)
    {
        if (s.Providers.Count == 0)
        {
            var p = ApiProvider.FromLegacy(s.Api);
            p.Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id;
            s.Providers.Add(p);
            if (string.IsNullOrWhiteSpace(s.DefaultModel.ProviderId) && !string.IsNullOrWhiteSpace(p.DefaultModel))
                s.DefaultModel = new ModelRef { ProviderId = p.Id, Model = p.DefaultModel };
            Log.Info($"模型设置已从旧的单 API 配置迁移：{p.Name}（{p.BaseUrl}）默认模型={s.DefaultModel.Model}");
            Save(s);
            return;
        }

        // 有供应商列表但没设默认模型：用第一个有模型的供应商兜底
        if (!s.DefaultModel.IsSet)
        {
            var p = s.Providers.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.DefaultModel) || x.Models.Count > 0)
                    ?? s.Providers[0];
            var model = !string.IsNullOrWhiteSpace(p.DefaultModel)
                ? p.DefaultModel
                : p.Models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "";
            if (model.Length > 0)
            {
                s.DefaultModel = new ModelRef { ProviderId = p.Id, Model = model.Trim() };
                Log.Info($"原配置没有默认模型，已补为 {p.Name} / {s.DefaultModel.Model}");
                Save(s);
            }
        }
    }

    /// <summary>把当前「默认模型」写回旧字段 <see cref="AppSettings.Api"/>，保证旧版本与自检命令仍可用。</summary>
    private static void SyncLegacyApi(AppSettings s)
    {
        var p = s.Providers.FirstOrDefault(x => string.Equals(x.Id, s.DefaultModel.ProviderId, StringComparison.Ordinal))
                ?? s.Providers.FirstOrDefault();
        if (p == null) return;
        var model = !string.IsNullOrWhiteSpace(s.DefaultModel.Model) ? s.DefaultModel.Model : p.DefaultModel;
        s.Api = new ApiSettings
        {
            BaseUrl = p.BaseUrl,
            ApiKey = p.ApiKey,
            Model = model,
            KnownModels = p.Models.ToList(),
            Temperature = p.Temperature,
            MaxTokens = p.MaxTokens,
            TimeoutSeconds = p.TimeoutSeconds,
            ImageMode = p.ImageMode,
        };
    }

    /// <summary>
    /// 老配置里没有「输入翻译」热键时补一条默认的（Ctrl+Alt+I），让升级后马上能用新功能；
    /// 只补一次：<see cref="GeneralSettings.InputTranslateHotkeyAdded"/> 为 true 后不再补，
    /// 用户在首选项里删掉它也不会自己回来。
    /// </summary>
    private void MigrateInputTranslateHotkey(AppSettings s)
    {
        if (s.General.InputTranslateHotkeyAdded) return;
        s.General.InputTranslateHotkeyAdded = true;
        if (s.Hotkeys.Any(h => string.Equals(h.Action, "InputTranslate", StringComparison.OrdinalIgnoreCase)))
            return;

        s.Hotkeys.Add(AppSettings.InputTranslateHotkey());
        Log.Info("已为「输入翻译」新功能自动添加默认热键 Ctrl+Alt+I");
        // 立刻落盘，避免用户没打开过首选项时每次启动都重新补一次
        Save(s);
    }
}
