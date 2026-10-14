using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickTranslate.Models;
using QuickTranslate.Services;

namespace QuickTranslate.Views;

public partial class PreferencesWindow : Window
{
    private readonly List<HotkeyRow> _rows = new();
    private CancellationTokenSource? _modelCts;

    /// <summary>正在把设置写进控件（或控件回写设置）时为 true，避免控件事件彼此覆盖。</summary>
    private bool _loading;
    private string? _currentProviderId;

    /// <summary>「添加 API」的常用预设（名称 / 地址 / 常用模型）。</summary>
    private static readonly (string Name, string BaseUrl, string Model)[] Presets =
    {
        ("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
        ("通义千问 DashScope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("本地 Ollama", "http://127.0.0.1:11434/v1", "qwen2.5:7b"),
        ("本地 LM Studio", "http://127.0.0.1:1234/v1", ""),
    };

    public PreferencesWindow() : this(null) { }

    /// <param name="initialTab">"models" = 直接打开「模型」页（调试/截图用）。</param>
    public PreferencesWindow(string? initialTab)
    {
        InitializeComponent();
        LoadSettingsIntoUi();
        if (string.Equals(initialTab, "models", StringComparison.OrdinalIgnoreCase))
            MainTabs.SelectedItem = ModelsTab;
        Closed += (_, _) =>
        {
            _modelCts?.Cancel();
            App.ResumeHotkeys();   // 关闭后恢复全局热键
        };
        // 打开首选项期间暂停全局热键，否则录入热键时会触发已有热键（冲突）
        App.SuspendHotkeys();
    }

    /// <summary>执行一段「只改控件、不回写设置」的操作。</summary>
    private void Quiet(Action a)
    {
        var prev = _loading;
        _loading = true;
        try { a(); } finally { _loading = prev; }
    }

    // ---------- 加载 ----------

    private void LoadSettingsIntoUi()
    {
        var s = App.Settings;

        AutoStartBox.IsChecked = s.General.AutoStart;
        SkipReasoningBox.IsChecked = s.General.SkipReasoningForTranslation;
        PosBox.SelectedIndex = s.General.PopupPosition == "Above" ? 0 : 1;
        TranslatePromptBox.Text = s.General.TranslatePrompt;
        ReplyPromptBox.Text = s.General.ReplyPrompt;
        ScreenshotPromptBox.Text = s.General.ScreenshotPrompt;

        _loading = true;
        try
        {
            RefreshProviderList(s.DefaultModel.ProviderId);
                HotkeysPanel.Children.Clear();
            _rows.Clear();
            foreach (var h in s.Hotkeys) AddRow(h);
            if (_rows.Count == 0) AddRow(new HotkeyEntry());
        }
        finally { _loading = false; }
    }

    // ---------- API 列表（左） ----------

    private sealed class ProviderItem
    {
        public ApiProvider Provider { get; }
        public ProviderItem(ApiProvider p) => Provider = p;

        public override string ToString()
        {
            var name = string.IsNullOrWhiteSpace(Provider.Name) ? "(未命名 API)" : Provider.Name;
            var model = string.IsNullOrWhiteSpace(Provider.DefaultModel) ? "未设置模型" : Provider.DefaultModel;
            return $"{name}\n{model}";
        }
    }

    private ApiProvider? SelectedProvider => (ProviderList.SelectedItem as ProviderItem)?.Provider;

    private void RefreshProviderList(string? selectId)
    {
        Quiet(() =>
        {
            ProviderList.Items.Clear();
            foreach (var p in App.Settings.Providers) ProviderList.Items.Add(new ProviderItem(p));

            int idx = 0;
            var target = selectId ?? _currentProviderId;
            if (ProviderList.Items.Count > 0 && !string.IsNullOrWhiteSpace(target))
            {
                for (int i = 0; i < ProviderList.Items.Count; i++)
                    if (((ProviderItem)ProviderList.Items[i]).Provider.Id == target) { idx = i; break; }
                ProviderList.SelectedIndex = idx;
            }
            LoadProviderDetail();
        });
        PopulateDefaultModelRow();
    }

    private void ProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        LoadProviderDetail();
    }

    private void LoadProviderDetail()
    {
        var p = SelectedProvider;
        ProviderDetailPanel.IsEnabled = p != null;
        DelProviderBtn.IsEnabled = p != null;
        if (p == null)
        {
            _currentProviderId = null;
            return;
        }

        _currentProviderId = p.Id;
        Quiet(() =>
        {
            ProviderNameBox.Text = p.Name;
            ProviderBaseUrlBox.Text = p.BaseUrl;
            ProviderKeyBox.Password = p.ApiKey;
            ProviderKeyText.Text = p.ApiKey;
            ProviderTempSlider.Value = Math.Clamp(p.Temperature, 0, 1.5);
            ProviderTempLabel.Text = p.Temperature.ToString("0.0");
            ProviderMaxTokensBox.Text = p.MaxTokens.ToString();
            ProviderTimeoutBox.Text = p.TimeoutSeconds.ToString();
            ProviderModelBox.Items.Clear();
            foreach (var m in p.ModelCandidates()) ProviderModelBox.Items.Add(m);
            ProviderModelBox.Text = p.DefaultModel;
            ProviderImgVisionRadio.IsChecked = p.ImageMode != "Ocr";
            ProviderImgOcrRadio.IsChecked = p.ImageMode == "Ocr";
            ProviderModelStatus.Text = "点「获取模型列表」可从接口拉取模型名（需先填好接口地址与 API Key），也可以直接在「常用模型」里手动输入。";
            ProviderModelStatus.Foreground = ThemeBrush("SubtleTextBrush", Color.FromRgb(0x6E, 0x8C, 0xAD));
            TestStatus.Text = "";
        });
    }

    /// <summary>把详情区控件的当前值写回选中的 API（编辑即生效，切换 API / 保存都不会丢）。</summary>
    private void CommitProviderDetail()
    {
        if (_loading) return;
        var p = SelectedProvider;
        if (p == null) return;

        p.Name = ProviderNameBox.Text.Trim();
        p.BaseUrl = ProviderBaseUrlBox.Text.Trim();
        p.ApiKey = ShowProviderKeyCheck.IsChecked == true ? ProviderKeyText.Text : ProviderKeyBox.Password;
        p.Temperature = Math.Round(ProviderTempSlider.Value, 2);
        if (int.TryParse(ProviderMaxTokensBox.Text, out var mt)) p.MaxTokens = Math.Clamp(mt, 1, 32768);
        if (int.TryParse(ProviderTimeoutBox.Text, out var to)) p.TimeoutSeconds = Math.Clamp(to, 5, 600);
        p.DefaultModel = ProviderModelBox.Text.Trim();
        if (ProviderImgOcrRadio.IsChecked == true) p.ImageMode = "Ocr";
        else if (ProviderImgVisionRadio.IsChecked == true) p.ImageMode = "Vision";

        ProviderList.Items.Refresh();
        PopulateDefaultModelRow();
    }

    private void ProviderField_Changed(object sender, RoutedEventArgs e) => CommitProviderDetail();

    private void ProviderTemp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 里 Slider 的初始 Value 会触发一次：那时后面的控件还没建好，必须判空
        if (ProviderTempLabel != null) ProviderTempLabel.Text = ProviderTempSlider.Value.ToString("0.0");
        CommitProviderDetail();
    }

    private void ShowProviderKey_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowProviderKeyCheck.IsChecked == true)
        {
            ProviderKeyText.Text = ProviderKeyBox.Password;
            ProviderKeyBox.Visibility = Visibility.Collapsed;
            ProviderKeyText.Visibility = Visibility.Visible;
        }
        else
        {
            ProviderKeyBox.Password = ProviderKeyText.Text;
            ProviderKeyBox.Visibility = Visibility.Visible;
            ProviderKeyText.Visibility = Visibility.Collapsed;
        }
        CommitProviderDetail();
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddProviderBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var preset in Presets)
        {
            var item = new MenuItem { Header = preset.Name + (preset.Model.Length > 0 ? $"（{preset.Model}）" : "") };
            item.Click += (_, _) => AddProvider(preset.Name, preset.BaseUrl, preset.Model);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var blank = new MenuItem { Header = "空白 API…" };
        blank.Click += (_, _) => AddProvider("", "", "");
        menu.Items.Add(blank);
        menu.IsOpen = true;
    }

    private void AddProvider(string name, string baseUrl, string model)
    {
        // 参数（温度 / 最大输出 Token / 超时 / 图片翻译方式）沿用当前选中的 API；API Key 不复制
        var src = SelectedProvider ?? App.Settings.Providers.FirstOrDefault();
        var p = new ApiProvider
        {
            Name = string.IsNullOrWhiteSpace(name) ? NextProviderName() : name,
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? (src?.BaseUrl ?? "https://api.openai.com/v1") : baseUrl,
            ApiKey = "",
            Temperature = src?.Temperature ?? 0.3,
            MaxTokens = src?.MaxTokens ?? 2048,
            TimeoutSeconds = src?.TimeoutSeconds ?? 60,
            ImageMode = src?.ImageMode ?? "Vision",
            DefaultModel = model,
        };
        if (!string.IsNullOrWhiteSpace(model)) p.Models.Add(model);

        App.Settings.Providers.Add(p);
        RefreshProviderList(p.Id);
        ProviderNameBox.Focus();
        ProviderNameBox.SelectAll();
        SetProviderStatus($"已添加「{p.Name}」，请填写 API Key（若需要）并点「获取模型列表」。", ok: true);
    }

    private string NextProviderName()
    {
        for (int i = 1; ; i++)
        {
            var name = $"API {i}";
            if (!App.Settings.Providers.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        var p = SelectedProvider;
        if (p == null) return;

        int refs = CountReferences(p.Id);
        var msg = $"确定删除 API「{p.Name}」吗？" +
                  (refs > 0
                      ? $"\n\n默认模型 / 热键模型 里共有 {refs} 处引用了它，删除后这些引用会一并清空（改用下一级，通常就是默认模型）。"
                      : "");
        if (MessageBox.Show(msg, "QuickTranslate", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        App.Settings.Providers.Remove(p);
        ClearReferences(p.Id);
        _currentProviderId = null;
        RefreshProviderList(App.Settings.Providers.FirstOrDefault()?.Id);
        RefreshHotkeyModelButtons();
    }

    private int CountReferences(string providerId)
    {
        int n = 0;
        if (string.Equals(App.Settings.DefaultModel.ProviderId, providerId, StringComparison.Ordinal)) n++;
        n += App.Settings.Hotkeys.Count(h => string.Equals(h.Model?.ProviderId, providerId, StringComparison.Ordinal));
        return n;
    }

    private void ClearReferences(string providerId)
    {
        if (string.Equals(App.Settings.DefaultModel.ProviderId, providerId, StringComparison.Ordinal))
            App.Settings.DefaultModel.Clear();
        foreach (var h in App.Settings.Hotkeys)
            if (h.Model != null && string.Equals(h.Model.ProviderId, providerId, StringComparison.Ordinal)) h.Model.Clear();
    }

    private void SetProviderStatus(string text, bool warn = false, bool ok = false)
    {
        ProviderModelStatus.Text = text;
        var key = warn ? "WarnTextBrush" : ok ? "OkTextBrush" : "SubtleTextBrush";
        ProviderModelStatus.Foreground = ThemeBrush(key, Color.FromRgb(0x6E, 0x8C, 0xAD));
    }

    // ---------- 默认模型 ----------

    private void PopulateDefaultModelRow()
    {
        Quiet(() =>
        {
            DefaultApiBox.Items.Clear();
            foreach (var p in App.Settings.Providers) DefaultApiBox.Items.Add(new ProviderItem(p));

            int idx = -1;
            for (int i = 0; i < DefaultApiBox.Items.Count; i++)
                if (((ProviderItem)DefaultApiBox.Items[i]).Provider.Id == App.Settings.DefaultModel.ProviderId) { idx = i; break; }
            if (idx < 0 && DefaultApiBox.Items.Count > 0) idx = 0;
            DefaultApiBox.SelectedIndex = idx;

            var current = (DefaultApiBox.SelectedItem as ProviderItem)?.Provider;
            PopulateModelCombo(DefaultModelBox, current, App.Settings.DefaultModel.Model);
            UpdateDefaultModelHint();
        });
    }

    private static void PopulateModelCombo(ComboBox combo, ApiProvider? p, string? current)
    {
        combo.Items.Clear();
        foreach (var m in p?.ModelCandidates() ?? new List<string>()) combo.Items.Add(m);
        var text = (current ?? "").Trim();
        if (text.Length > 0 && !combo.Items.Contains(text)) combo.Items.Add(text);
        combo.Text = text;
        combo.IsEnabled = p != null;
    }

    private void UpdateDefaultModelHint()
    {
        var p = (DefaultApiBox.SelectedItem as ProviderItem)?.Provider;
        if (p == null)
        {
            DefaultModelHint.Text = "还没有 API：请先在左侧「＋ 添加」。";
            return;
        }
        var m = DefaultModelBox.Text.Trim();
        DefaultModelHint.Text = m.Length == 0
            ? "请选择或输入模型名"
            : $"所有没单独指定模型的请求都会用 {p.Name} / {m}";
    }

    private void DefaultApi_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var p = (DefaultApiBox.SelectedItem as ProviderItem)?.Provider;
        App.Settings.DefaultModel.ProviderId = p?.Id ?? "";
        // 换了 API，模型也要跟着换成新 API 下的（优先它的常用模型）
        var keep = App.Settings.DefaultModel.Model;
        var candidates = p?.ModelCandidates() ?? new List<string>();
        if (!candidates.Contains(keep, StringComparer.OrdinalIgnoreCase))
            keep = candidates.FirstOrDefault() ?? "";
        App.Settings.DefaultModel.Model = keep;
        Quiet(() =>
        {
            PopulateModelCombo(DefaultModelBox, p, keep);
            UpdateDefaultModelHint();
        });
    }

    private void DefaultModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (DefaultModelBox.SelectedItem is string s)
        {
            App.Settings.DefaultModel.Model = s;
            UpdateDefaultModelHint();
            }
    }

    private void DefaultModel_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Settings.DefaultModel.Model = DefaultModelBox.Text.Trim();
        UpdateDefaultModelHint();
    }

    // ---------- 获取模型列表 ----------

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var p = SelectedProvider;
        if (p == null) return;
        CommitProviderDetail();

        if (string.IsNullOrWhiteSpace(p.BaseUrl))
        {
            SetProviderStatus("请先填写接口地址 (Base URL)。", warn: true);
            ProviderBaseUrlBox.Focus();
            return;
        }

        _modelCts?.Cancel();
        _modelCts = new CancellationTokenSource();
        var ct = _modelCts.Token;

        FetchModelsBtn.IsEnabled = false;
        var oldContent = FetchModelsBtn.Content;
        FetchModelsBtn.Content = "获取中…";
        SetProviderStatus("正在从接口获取模型列表…");

        try
        {
            var models = await new OpenAIClient(p.ToApiSettings(p.DefaultModel)).ListModelsAsync(ct);
            if (ct.IsCancellationRequested) return;
            if (models.Count == 0)
            {
                SetProviderStatus("接口没有返回任何模型，可手动输入模型名。", warn: true);
                return;
            }

            p.Models = models;
            if (string.IsNullOrWhiteSpace(p.DefaultModel) || !models.Contains(p.DefaultModel, StringComparer.OrdinalIgnoreCase))
                p.DefaultModel = models[0];

            Quiet(() =>
            {
                ProviderModelBox.Items.Clear();
                foreach (var m in p.ModelCandidates()) ProviderModelBox.Items.Add(m);
                ProviderModelBox.Text = p.DefaultModel;
            });
            PopulateDefaultModelRow();
                App.SettingsService.Save(App.Settings);   // 立刻落盘，避免忘记保存而丢列表
            SetProviderStatus($"获取成功：共 {models.Count} 个模型，已填入「常用模型」下拉框。", ok: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetProviderStatus("获取失败：" + ex.Message, warn: true);
            Log.Error(ex);
        }
        finally
        {
            FetchModelsBtn.Content = oldContent;
            FetchModelsBtn.IsEnabled = true;
        }
    }

    // ---------- 测试连接 ----------

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var p = SelectedProvider;
        if (p == null) return;
        CommitProviderDetail();

        var model = ProviderModelBox.Text.Trim();
        if (model.Length == 0)
        {
            TestStatus.Text = "请先填写「常用模型」（或点「获取模型列表」）。";
            return;
        }

        TestBtn.IsEnabled = false;
        TestStatus.Text = "测试中…";
        try
        {
            var client = new OpenAIClient(p.ToApiSettings(model));
            TestStatus.Text = await client.TestAsync();
        }
        catch (Exception ex)
        {
            TestStatus.Text = "失败：" + ex.Message;
        }
        finally { TestBtn.IsEnabled = true; }
    }

    // ---------- 热键 ----------

    private void AddHotkey_Click(object sender, RoutedEventArgs e) => AddRow(new HotkeyEntry());

    private void AddRow(HotkeyEntry h)
    {
        h.Model ??= new ModelRef();
        var row = new HotkeyRow(h);
        row.DeleteRequested += r =>
        {
            HotkeysPanel.Children.Remove(r.Root);
            _rows.Remove(r);
        };
        HotkeysPanel.Children.Add(row.Root);
        _rows.Add(row);
    }

    private void RefreshHotkeyModelButtons()
    {
        foreach (var r in _rows) r.RefreshModelButton();
    }

    // ---------- 保存 ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;

        // 模型页：编辑即写回，这里只做一次兜底同步（编辑框里的文字）
        CommitProviderDetail();
        s.DefaultModel.Model = DefaultModelBox.Text.Trim();
        foreach (var r in _rows) r.Commit();

        if (s.Providers.Count == 0)
        {
            MessageBox.Show("请至少添加一个 API（首选项 → 模型）。", "QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            MainTabs.SelectedItem = ModelsTab;
            return;
        }
        if (!s.DefaultModel.IsSet)
        {
            MessageBox.Show("请设置「默认模型」（首选项 → 模型）：选择 API 与模型后再保存。", "QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            MainTabs.SelectedItem = ModelsTab;
            return;
        }
        if (s.Providers.Any(p => string.IsNullOrWhiteSpace(p.BaseUrl)))
        {
            MessageBox.Show("有一个 API 没填接口地址，请补全后再保存。", "QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            MainTabs.SelectedItem = ModelsTab;
            return;
        }

        s.General.AutoStart = AutoStartBox.IsChecked == true;
        s.General.SkipReasoningForTranslation = SkipReasoningBox.IsChecked == true;
        s.General.PopupPosition = PosBox.SelectedIndex == 0 ? "Above" : "Auto";
        s.General.TranslatePrompt = TranslatePromptBox.Text.Trim();
        s.General.ReplyPrompt = ReplyPromptBox.Text.Trim();
        s.General.ScreenshotPrompt = ScreenshotPromptBox.Text.Trim();
        // 旧字段跟随翻译提示词，避免旧版本读到过时的全局提示词
        s.General.CustomSystemPrompt = s.General.TranslatePrompt;

        // 收集并校验热键
        var list = new List<HotkeyEntry>();
        var seen = new HashSet<(int Mod, int Vk)>();
        foreach (var r in _rows)
        {
            r.Commit(); // 把可编辑下拉框当前文本写回条目
            if (string.IsNullOrWhiteSpace(r.Entry.Name)) continue;
            if (r.Entry.KeyVk == 0)
            {
                MessageBox.Show($"热键“{r.Entry.Name}”未设置按键。", "QuickTranslate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var key = (r.Entry.Modifiers, r.Entry.KeyVk);
            if (seen.Contains(key))
            {
                MessageBox.Show($"热键 {r.Entry.Display} 重复，请修改。", "QuickTranslate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            seen.Add(key);
            list.Add(r.Entry);
        }
        if (list.Count == 0) list = AppSettings.DefaultHotkeys();
        s.Hotkeys = list;

        App.SettingsService.Save(s);
        ((App)System.Windows.Application.Current).ApplySettings();
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>取主题画刷，取不到时用兜底色，保证代码构建的控件也有统一外观。</summary>
    internal static Brush ThemeBrush(string key, Color fallback)
        => System.Windows.Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}


/// <summary>热键编辑行（代码构建，便于热键捕获交互）。</summary>
public sealed class HotkeyRow
{
    public HotkeyEntry Entry { get; }
    public Border Root { get; }
    public event Action<HotkeyRow>? DeleteRequested;

    private readonly TextBox _hotkeyBox;
    private readonly Button _promptBtn;
    private readonly Button _modelBtn;
    private ComboBox? _langBox;

    /// <summary>把可编辑的目标语言下拉框当前文本写回条目（保存时调用）。</summary>
    public void Commit()
    {
        if (_langBox != null) Entry.TargetLanguage = _langBox.Text;
    }

    public HotkeyRow(HotkeyEntry entry)
    {
        Entry = entry;
        Entry.Model ??= new ModelRef();

        var grid = new Grid();
        foreach (var width in new[] { 104d, 96d, 118d, 122d, 58d, 58d, 40d, 28d })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });

        // 名称
        var nameBox = new TextBox { Text = Entry.Name, VerticalContentAlignment = VerticalAlignment.Center };
        nameBox.TextChanged += (_, _) => Entry.Name = nameBox.Text;
        Grid.SetColumn(nameBox, 0);
        grid.Children.Add(nameBox);

        // 动作
        var actionBox = new ComboBox { VerticalContentAlignment = VerticalAlignment.Center };
        actionBox.Items.Add("翻译");
        actionBox.Items.Add("生成回复");
        actionBox.Items.Add("截图翻译");
        actionBox.Items.Add("输入翻译");
        actionBox.SelectedIndex = Entry.Action switch
        {
            "GenerateReply" => 1,
            "ScreenshotOcr" => 2,
            "InputTranslate" => 3,
            _ => 0,
        };
        actionBox.SelectionChanged += (_, _) =>
        {
            Entry.Action = actionBox.SelectedIndex switch
            {
                1 => "GenerateReply",
                2 => "ScreenshotOcr",
                3 => "InputTranslate",
                _ => "Translate",
            };
            RefreshModelButton();   // 动作/语言变了，按钮上的提示跟着刷新
        };
        Grid.SetColumn(actionBox, 1);
        grid.Children.Add(actionBox);

        // 目标语言（可编辑）
        var langBox = new ComboBox
        {
            IsEditable = true,
            Text = Entry.TargetLanguage,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        foreach (var l in new[] { "中文", "English", "日本語", "한국어", "Français", "Deutsch", "Русский", "Español" })
            langBox.Items.Add(l);
        _langBox = langBox;
        Grid.SetColumn(langBox, 2);
        grid.Children.Add(langBox);

        // 热键捕获框
        _hotkeyBox = new TextBox
        {
            Text = Entry.Display,
            IsReadOnly = true,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = PreferencesWindow.ThemeBrush("AccentSoftBrush", Color.FromRgb(0xE4, 0xEF, 0xFE)),
            ToolTip = "点击后按下新的组合键",
        };
        _hotkeyBox.PreviewKeyDown += HotkeyBox_PreviewKeyDown;
        _hotkeyBox.GotKeyboardFocus += (_, _) => _hotkeyBox.Text = "请按下组合键…";
        _hotkeyBox.LostKeyboardFocus += (_, _) => _hotkeyBox.Text = Entry.Display;
        Grid.SetColumn(_hotkeyBox, 3);
        grid.Children.Add(_hotkeyBox);

        // 提示词（该热键专用，留空则使用「提示词」页里对应功能的默认提示词）
        _promptBtn = SmallButton(54);
        _promptBtn.Content = "提示词";
        _promptBtn.Click += (_, _) => EditPrompt();
        RefreshPromptButton();
        Grid.SetColumn(_promptBtn, 4);
        grid.Children.Add(_promptBtn);

        // 模型（该热键专用；不设则用「模型」页里该功能的模型）
        _modelBtn = SmallButton(54);
        _modelBtn.Content = "模型";
        _modelBtn.Click += (_, _) => EditModel();
        RefreshModelButton();
        Grid.SetColumn(_modelBtn, 5);
        grid.Children.Add(_modelBtn);

        // 启用
        var enableBox = new CheckBox
        {
            IsChecked = Entry.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        enableBox.Checked += (_, _) => Entry.Enabled = true;
        enableBox.Unchecked += (_, _) => Entry.Enabled = false;
        Grid.SetColumn(enableBox, 6);
        grid.Children.Add(enableBox);

        // 删除
        var delBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            FontSize = 12,
            ToolTip = "删除该热键",
        };
        if (System.Windows.Application.Current?.TryFindResource("IconButtonStyle") is Style iconStyle)
            delBtn.Style = iconStyle;
        delBtn.Click += (_, _) => DeleteRequested?.Invoke(this);
        Grid.SetColumn(delBtn, 7);
        grid.Children.Add(delBtn);

        // 行容器：淡蓝圆角卡片
        Root = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = PreferencesWindow.ThemeBrush("CardSoftBrush", Color.FromRgb(0xF7, 0xFB, 0xFF)),
            BorderBrush = PreferencesWindow.ThemeBrush("LineBrush", Color.FromRgb(0xC9, 0xDD, 0xF6)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
    }

    private static Button SmallButton(double width) => new()
    {
        Width = width,
        Height = 28,
        FontSize = 11.5,
        Padding = new Thickness(2, 0, 2, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>为该热键单独设置系统提示词（留空 = 使用对应功能的默认提示词）。</summary>
    private void EditPrompt()
    {
        var sub = Entry.Action switch
        {
            "GenerateReply" => "只对这条「生成回复」热键生效；留空则使用「提示词」页里“生成回复提示词”的默认内容。",
            "ScreenshotOcr" => "只对这条「截图翻译」热键生效；留空则使用「提示词」页里“截图翻译提示词”的默认内容。",
            "InputTranslate" => "只对这条「输入翻译」热键生效（按下它弹出的输入框里翻译时使用）；留空则使用「提示词」页里“翻译提示词”的默认内容。",
            _ => "只对这条「翻译」热键生效；留空则使用「提示词」页里“翻译提示词”的默认内容。",
        };
        var edited = PromptEditorWindow.Edit(Window.GetWindow(Root), $"提示词 · {Entry.Name}", sub, Entry.Prompt);
        if (edited == null) return;   // 取消
        Entry.Prompt = edited;
        RefreshPromptButton();
    }

    private void RefreshPromptButton()
    {
        bool has = Entry.HasPrompt;
        _promptBtn.Content = has ? "提示词 ●" : "提示词";
        _promptBtn.Background = has
            ? PreferencesWindow.ThemeBrush("AccentSoftBrush", Color.FromRgb(0xE4, 0xEF, 0xFE))
            : PreferencesWindow.ThemeBrush("CardBrush", Colors.White);
        _promptBtn.Foreground = has
            ? PreferencesWindow.ThemeBrush("AccentBrush", Color.FromRgb(0x4A, 0x8C, 0xF7))
            : PreferencesWindow.ThemeBrush("SubtleTextBrush", Color.FromRgb(0x6E, 0x8C, 0xAD));
        _promptBtn.ToolTip = has
            ? "已设置该热键专用提示词（点击可修改）：\n" + Shorten(Entry.Prompt, 180)
            : "点击为该热键单独设置系统提示词（留空使用功能默认）";
    }

    /// <summary>为该热键单独指定 API + 模型（不设 = 用该功能的模型）。</summary>
    private void EditModel()
    {
        Log.Info($"模型选择: 打开对话框（热键“{Entry.Name}” 当前={DescribeEffective()}）");
        var picked = ModelPickerWindow.Pick(Window.GetWindow(Root), Entry,
            $"当前生效：{DescribeEffective()}");
        if (picked == null)
        {
            Log.Info("模型选择: 已取消");
            return;
        }
        Entry.Model = picked;         // 可能是「跟随默认模型」（空引用）
        Log.Info($"模型选择: 已设为 {(picked.IsSet ? App.Models.Describe(picked) : "跟随默认模型")}");
        RefreshModelButton();
    }

    /// <summary>这条热键最终会用哪个模型（热键 > 默认），解析失败时给出提示文案。</summary>
    private string DescribeEffective()
    {
        try
        {
            var sel = App.Models.Resolve(Entry);
            var source = Entry.HasModel ? "本热键指定" : $"来自 {sel.Source}";
            return $"{sel.Display}（{source}）";
        }
        catch (Exception ex)
        {
            return "还不是可用模型：" + ex.Message;
        }
    }

    public void RefreshModelButton()
    {
        bool has = Entry.HasModel;
        _modelBtn.Content = has ? "模型 ●" : "模型";
        _modelBtn.Background = has
            ? PreferencesWindow.ThemeBrush("AccentSoftBrush", Color.FromRgb(0xE4, 0xEF, 0xFE))
            : PreferencesWindow.ThemeBrush("CardBrush", Colors.White);
        _modelBtn.Foreground = has
            ? PreferencesWindow.ThemeBrush("AccentBrush", Color.FromRgb(0x4A, 0x8C, 0xF7))
            : PreferencesWindow.ThemeBrush("SubtleTextBrush", Color.FromRgb(0x6E, 0x8C, 0xAD));
        _modelBtn.ToolTip = (has ? "已单独指定模型（点击可修改）\n" : "点击为这条热键单独指定 API 与模型（不设则跟随默认模型）\n") +
                            DescribeEffective();
    }

    private static string Shorten(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        int mods = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= 8;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;

        if (mods == 0)
        {
            _hotkeyBox.Text = "需要至少一个修饰键（Ctrl/Alt/Shift/Win）";
            return;
        }

        Entry.Modifiers = mods;
        Entry.KeyVk = KeyInterop.VirtualKeyFromKey(key);
        _hotkeyBox.Text = Entry.Display;
    }
}
