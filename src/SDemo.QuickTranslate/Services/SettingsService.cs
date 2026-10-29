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
                    MigratePlaceholderProvider(s);
                    MigrateInputTranslateHotkey(s);
                    MigrateAutoStartDefault(s);
                    MigrateDefaultHotkeyLanguages(s);
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
            var p = LooksLikeFactoryLegacy(s.Api) ? ApiProvider.DefaultProvider() : ApiProvider.FromLegacy(s.Api);
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

    /// <summary>旧版配置仍是出厂默认值（OpenAI / gpt-4o-mini、没有 Key）：说明用户从没配置过，直接用内置 GPUStack 默认值。</summary>
    private static bool LooksLikeFactoryLegacy(ApiSettings a)
        => string.IsNullOrWhiteSpace(a.ApiKey)
           && string.Equals((a.BaseUrl ?? "").Trim().TrimEnd('/'), "https://api.openai.com/v1", StringComparison.OrdinalIgnoreCase)
           && string.Equals(a.Model, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase)
           && (a.KnownModels == null || a.KnownModels.Count == 0);

    /// <summary>
    /// 上一版首次运行会生成一个「OpenAI / gpt-4o-mini / 没有 Key」的占位供应商；
    /// 这里把它换成内置 GPUStack 默认值，保证升级后开箱就是一个能用的默认模型。
    /// 用户填过 Key 或改过地址/模型时不动。
    /// </summary>
    private void MigratePlaceholderProvider(AppSettings s)
    {
        if (s.Providers.Count != 1) return;
        var p = s.Providers[0];
        if (!string.IsNullOrWhiteSpace(p.ApiKey)) return;
        if (!p.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(p.DefaultModel, "gpt-4o-mini", StringComparison.OrdinalIgnoreCase)) return;
        if (p.Models.Count > 1) return;
        if (p.Models.Count == 1 && !string.Equals(p.Models[0], "gpt-4o-mini", StringComparison.OrdinalIgnoreCase)) return;

        var fresh = ApiProvider.DefaultProvider();
        fresh.Id = p.Id;   // 保留 Id，尽量少让已有引用失效
        s.Providers[0] = fresh;

        if (string.IsNullOrWhiteSpace(s.DefaultModel.ProviderId) ||
            string.Equals(s.DefaultModel.ProviderId, p.Id, StringComparison.Ordinal))
            s.DefaultModel = new ModelRef { ProviderId = fresh.Id, Model = fresh.DefaultModel };

        foreach (var h in s.Hotkeys)
            if (h.Model != null && string.Equals(h.Model.ProviderId, p.Id, StringComparison.Ordinal) &&
                !fresh.ModelCandidates().Contains(h.Model.Model, StringComparer.OrdinalIgnoreCase))
                h.Model.Clear();

        Log.Info($"默认供应商已从占位 OpenAI 配置换成内置 GPUStack：{fresh.BaseUrl}");
        Save(s);
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

    /// <summary>
    /// 开机自启改为默认开启：老配置里即使存过 false，也按新默认值开启一次（用户之后在界面上关掉就不会再被改回来）。
    /// 是否真的写入注册表由启动时的 <c>ApplyAutoStart</c> 按这里的值同步。
    /// </summary>
    private void MigrateAutoStartDefault(AppSettings s)
    {
        if (s.General.AutoStartDefaultApplied) return;
        s.General.AutoStartDefaultApplied = true;
        s.General.AutoStart = true;
        Log.Info("已按新的默认值开启「开机自动启动」（可在 首选项 → 通用 里关闭）");
        Save(s);
    }

    /// <summary>
    /// 老配置里「生成回复 / 输入翻译」的目标语言是空（沿用上次/中文）。
    /// 按新默认补成中文与英文；只迁移一次，之后保留用户在界面上改过的值。
    /// </summary>
    private void MigrateDefaultHotkeyLanguages(AppSettings s)
    {
        if (s.General.DefaultLanguagesApplied) return;
        s.General.DefaultLanguagesApplied = true;
        foreach (var h in s.Hotkeys)
        {
            if (!string.IsNullOrWhiteSpace(h.TargetLanguage)) continue;
            if (string.Equals(h.Action, "GenerateReply", StringComparison.OrdinalIgnoreCase))
                h.TargetLanguage = "中文";
            else if (string.Equals(h.Action, "InputTranslate", StringComparison.OrdinalIgnoreCase))
                h.TargetLanguage = "English";
        }
        Save(s);
    }
}
