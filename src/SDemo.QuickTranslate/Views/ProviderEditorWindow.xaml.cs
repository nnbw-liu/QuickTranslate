using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SDemo.QuickTranslate.Models;
using SDemo.QuickTranslate.Services;

namespace SDemo.QuickTranslate.Views;

/// <summary>
/// 添加 / 编辑一个模型提供商（OpenAI 兼容接口）。
/// 卡片式字段：基本信息、API Key、模型目录、高级参数；保存后 API Key 不再显示。
/// </summary>
public partial class ProviderEditorWindow : Window
{
    private static readonly (string Name, string BaseUrl, string Model)[] Presets =
    {
        ("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
        ("通义千问 DashScope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("本地 Ollama", "http://127.0.0.1:11434/v1", "qwen2.5:7b"),
        ("本地 LM Studio", "http://127.0.0.1:1234/v1", ""),
    };

    private readonly ApiProvider? _existing;
    private readonly string _originalKey;
    private readonly ObservableCollection<string> _models = new();
    private CancellationTokenSource? _fetchCts;
    private bool _loading;
    private ApiProvider? _result;

    private ProviderEditorWindow(ApiProvider? existing, string? suggestedName)
    {
        InitializeComponent();
        FitToWorkArea();
        _existing = existing;
        _originalKey = existing?.ApiKey ?? "";

        HeadText.Text = existing == null ? "添加模型提供商" : "编辑模型提供商";
        SubText.Text = "只支持标准 OpenAI 兼容接口（GET /v1/models、POST /v1/chat/completions）。" +
                       (existing == null ? "" : "\n保存后修改生效；已保存的 API Key 不会显示。");

        ModelList.ItemsSource = _models;
        RecordUi(() =>
        {
            PresetRow.Visibility = existing == null ? Visibility.Visible : Visibility.Collapsed;
            PresetBox.Items.Add("空白（手动填写）");
            foreach (var preset in Presets)
                PresetBox.Items.Add(preset.Model.Length > 0 ? $"{preset.Name}（{preset.Model}）" : preset.Name);
            PresetBox.SelectedIndex = 0;

            NameBox.Text = existing?.Name ?? suggestedName ?? "";
            BaseUrlBox.Text = existing?.BaseUrl ?? "";
            TempSlider.Value = Math.Clamp(existing?.Temperature ?? 0.3, 0, 1.5);
            TempLabel.Text = ((existing?.Temperature ?? 0.3)).ToString("0.0");
            MaxTokensBox.Text = (existing?.MaxTokens ?? 2048).ToString();
            TimeoutBox.Text = (existing?.TimeoutSeconds ?? 60).ToString();
            ImgVisionRadio.IsChecked = existing?.ImageMode != "Ocr";
            ImgOcrRadio.IsChecked = existing?.ImageMode == "Ocr";

            if (existing != null)
                foreach (var m in existing.ModelCandidates()) _models.Add(m);

            RefreshDefaultModelOptions(existing?.DefaultModel ?? "");

            // API Key 一律不回填密码框，避免保存后还能看到
            KeyBox.Password = "";
            if (_originalKey.Length > 0)
            {
                KeyHint.Text = "已保存的 API Key 不会显示。留空 = 保持不变；输入新内容 = 替换。";
                ClearKeyBox.Visibility = Visibility.Visible;
            }
            else
            {
                KeyHint.Text = "本地服务可留空；保存后不再显示。";
            }

            FetchStatus.Text = "点「获取模型列表」可从接口拉取模型名（需先填好接口地址与 API Key），也可以手动添加。";
            TestStatus.Text = "";
        });

        Closed += (_, _) => _fetchCts?.Cancel();
    }

    /// <summary>打开编辑对话框；返回 null = 取消，否则返回要保存的提供商（新对象，未加入列表）。</summary>
    public static ApiProvider? Edit(Window? owner, ApiProvider? existing, string? suggestedName = null)
    {
        var w = new ProviderEditorWindow(existing, suggestedName);
        if (owner is { IsLoaded: true }) w.Owner = owner;
        w.ShowDialog();
        return w._result;
    }

    private void RecordUi(Action a)
    {
        var prev = _loading;
        _loading = true;
        try { a(); } finally { _loading = prev; }
    }

    // ---------- 预设 ----------

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _existing != null) return;
        int index = PresetBox.SelectedIndex;
        if (index <= 0) return;
        var preset = Presets[index - 1];
        NameBox.Text = preset.Name;
        BaseUrlBox.Text = preset.BaseUrl;
        if (preset.Model.Length > 0)
        {
            if (!_models.Any(m => string.Equals(m, preset.Model, StringComparison.OrdinalIgnoreCase)))
                _models.Add(preset.Model);
            RefreshDefaultModelOptions(preset.Model);
        }
    }

    // ---------- 模型目录 ----------

    private void AddModel_Click(object sender, RoutedEventArgs e) => AddModelFromInput();

    private void ModelInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddModelFromInput();
            e.Handled = true;
        }
    }

    private void AddModelFromInput()
    {
        var model = ModelInput.Text.Trim();
        if (model.Length == 0) return;
        if (_models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)))
        {
            ModelInput.SelectAll();
            return;
        }
        _models.Add(model);
        var keep = DefaultModelBox.Text.Trim();
        RefreshDefaultModelOptions(keep.Length > 0 ? keep : model);
        ModelInput.Clear();
        ModelInput.Focus();
    }

    private void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string model }) return;
        var keep = DefaultModelBox.Text.Trim();
        _models.Remove(model);
        if (string.Equals(keep, model, StringComparison.OrdinalIgnoreCase))
            keep = _models.FirstOrDefault() ?? "";
        RefreshDefaultModelOptions(keep);
    }

    private void RefreshDefaultModelOptions(string? keep = null)
    {
        var text = (keep ?? DefaultModelBox.Text).Trim();
        RecordUi(() =>
        {
            DefaultModelBox.Items.Clear();
            foreach (var m in _models) DefaultModelBox.Items.Add(m);
            if (text.Length > 0 && !DefaultModelBox.Items.Contains(text)) DefaultModelBox.Items.Add(text);
            DefaultModelBox.Text = text;
        });
    }

    // ---------- 获取模型列表 ----------

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var url = NormalizeBaseUrl(BaseUrlBox.Text);
        if (url == null)
        {
            SetStatus(FetchStatus, "请先填写有效的接口地址（http 或 https）。", warn: true);
            BaseUrlBox.Focus();
            return;
        }

        _fetchCts?.Cancel();
        _fetchCts = new CancellationTokenSource();
        var ct = _fetchCts.Token;

        FetchBtn.IsEnabled = false;
        var oldContent = FetchBtn.Content;
        FetchBtn.Content = "获取中…";
        SetStatus(FetchStatus, "正在从接口获取模型列表…");

        try
        {
            var models = await new OpenAIClient(BuildApiSettings(url, DefaultModelBox.Text.Trim())).ListModelsAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (models.Count == 0)
            {
                SetStatus(FetchStatus, "接口没有返回任何模型，可手动添加模型名。", warn: true);
                return;
            }

            _models.Clear();
            foreach (var m in models) _models.Add(m);

            var preferred = DefaultModelBox.Text.Trim();
            var keep = models.Contains(preferred, StringComparer.OrdinalIgnoreCase) ? preferred : models[0];
            RefreshDefaultModelOptions(keep);
            SetStatus(FetchStatus, $"获取成功：共 {models.Count} 个模型。", ok: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus(FetchStatus, "获取失败：" + ex.Message, warn: true);
            Log.Error(ex);
        }
        finally
        {
            FetchBtn.Content = oldContent;
            FetchBtn.IsEnabled = true;
        }
    }

    // ---------- 测试连接 ----------

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var url = NormalizeBaseUrl(BaseUrlBox.Text);
        if (url == null)
        {
            SetStatus(TestStatus, "请先填写有效的接口地址（http 或 https）。", warn: true);
            BaseUrlBox.Focus();
            return;
        }

        var model = DefaultModelBox.Text.Trim();
        if (model.Length == 0) model = _models.FirstOrDefault() ?? "";
        if (model.Length == 0)
        {
            SetStatus(TestStatus, "请先添加或获取一个模型（测试连接需要一个模型名）。", warn: true);
            return;
        }

        TestBtn.IsEnabled = false;
        SetStatus(TestStatus, "测试中…");
        try
        {
            var result = await new OpenAIClient(BuildApiSettings(url, model)).TestAsync();
            SetStatus(TestStatus, result, ok: true);
        }
        catch (Exception ex)
        {
            SetStatus(TestStatus, "失败：" + ex.Message, warn: true);
        }
        finally { TestBtn.IsEnabled = true; }
    }

    // ---------- 保存 ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var url = NormalizeBaseUrl(BaseUrlBox.Text);
        if (url == null)
        {
            MessageBox.Show("请填写有效的接口地址（http 或 https）。", "SDemo.QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            BaseUrlBox.Focus();
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = ApiProvider.GuessName(url);

        var models = _models
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preferred = DefaultModelBox.Text.Trim();
        if (preferred.Length > 0 && !models.Contains(preferred, StringComparer.OrdinalIgnoreCase))
            models.Insert(0, preferred);

        var p = _existing?.Clone() ?? new ApiProvider();
        p.Name = name;
        p.BaseUrl = url;
        p.ApiKey = EffectiveKey();
        p.Models = models;
        p.DefaultModel = preferred;
        p.Temperature = Math.Round(TempSlider.Value, 2);
        p.MaxTokens = ParseInt(MaxTokensBox.Text, 2048, 1, 32768);
        p.TimeoutSeconds = ParseInt(TimeoutBox.Text, 60, 5, 600);
        p.ImageMode = ImgOcrRadio.IsChecked == true ? "Ocr" : "Vision";

        _result = p;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ---------- 工具 ----------

    /// <summary>按当前屏幕的工作区限制窗口尺寸：小屏笔记本上自动压矮，内容用滚动条查看。</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MaxHeight = Math.Max(MinHeight, area.Height - 32);
        if (Height > MaxHeight) Height = MaxHeight;
        if (Width > area.Width - 32) Width = Math.Max(MinWidth, area.Width - 32);
    }

    /// <summary>把用户输入的地址规范成 OpenAI 兼容的 Base URL（允许自动补 /v1）。</summary>
    private static string? NormalizeBaseUrl(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        text = text.TrimEnd('/');
        return uri.AbsolutePath is "" or "/" ? text + "/v1" : text;
    }

    private ApiSettings BuildApiSettings(string url, string model) => new()
    {
        BaseUrl = url,
        ApiKey = EffectiveKey(),
        Model = model,
        KnownModels = _models.ToList(),
        Temperature = Math.Round(TempSlider.Value, 2),
        MaxTokens = ParseInt(MaxTokensBox.Text, 2048, 1, 32768),
        TimeoutSeconds = ParseInt(TimeoutBox.Text, 60, 5, 600),
        ImageMode = ImgOcrRadio.IsChecked == true ? "Ocr" : "Vision",
    };

    /// <summary>输入的新 Key &gt; 清除勾选 &gt; 原有 Key。</summary>
    private string EffectiveKey()
    {
        if (ClearKeyBox.IsChecked == true) return "";
        var typed = KeyBox.Password;
        return string.IsNullOrWhiteSpace(typed) ? _originalKey : typed;
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
        => int.TryParse((text ?? "").Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;

    private void Temp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TempLabel != null) TempLabel.Text = TempSlider.Value.ToString("0.0");
    }

    private void AdvancedToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (AdvancedBody == null) return;
        var expand = AdvancedToggle.IsChecked == true;
        AdvancedBody.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        if (expand) Dispatcher.BeginInvoke(new Action(() => AdvancedBody.BringIntoView()));
    }

    private void SetStatus(TextBlock target, string text, bool warn = false, bool ok = false)
    {
        target.Text = text;
        var key = warn ? "WarnTextBrush" : ok ? "OkTextBrush" : "SubtleTextBrush";
        target.Foreground = Application.Current?.TryFindResource(key) as Brush
                            ?? new SolidColorBrush(Color.FromRgb(0x6E, 0x8C, 0xAD));
    }
}
