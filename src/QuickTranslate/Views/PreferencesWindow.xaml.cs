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

    /// <summary>内置常用模型（接口未拉取列表时的下拉候选）。</summary>
    private static readonly string[] BuiltInModels =
    {
        "gpt-4o", "gpt-4o-mini", "gpt-4-turbo", "gpt-3.5-turbo", "o1-mini", "o1",
        "deepseek-chat", "deepseek-reasoner", "qwen-plus", "glm-4-flash",
    };

    public PreferencesWindow()
    {
        InitializeComponent();
        LoadSettingsIntoUi();
        TempSlider.ValueChanged += (_, _) => TempLabel.Text = TempSlider.Value.ToString("0.0");
        Closed += (_, _) =>
        {
            _modelCts?.Cancel();
            App.ResumeHotkeys();   // 关闭后恢复全局热键
        };
        // 打开首选项期间暂停全局热键，否则录入热键时会触发已有热键（冲突）
        App.SuspendHotkeys();
    }

    // ---------- 加载 ----------

    private void LoadSettingsIntoUi()
    {
        var s = App.Settings;
        BaseUrlBox.Text = s.Api.BaseUrl;
        ApiKeyBox.Password = s.Api.ApiKey;
        ApiKeyText.Text = s.Api.ApiKey;
        PopulateModelBox(s.Api.Model);
        TempSlider.Value = s.Api.Temperature;
        TempLabel.Text = s.Api.Temperature.ToString("0.0");
        MaxTokensBox.Text = s.Api.MaxTokens.ToString();
        TimeoutBox.Text = s.Api.TimeoutSeconds.ToString();
        ImgVisionRadio.IsChecked = s.Api.ImageMode != "Ocr";
        ImgOcrRadio.IsChecked = s.Api.ImageMode == "Ocr";

        AutoStartBox.IsChecked = s.General.AutoStart;
        SkipReasoningBox.IsChecked = s.General.SkipReasoningForTranslation;
        PosBox.SelectedIndex = s.General.PopupPosition == "Above" ? 0 : 1;
        TranslatePromptBox.Text = s.General.TranslatePrompt;
        ReplyPromptBox.Text = s.General.ReplyPrompt;
        ScreenshotPromptBox.Text = s.General.ScreenshotPrompt;

        HotkeysPanel.Children.Clear();
        _rows.Clear();
        foreach (var h in s.Hotkeys) AddRow(h);
        if (_rows.Count == 0) AddRow(new HotkeyEntry());
    }

    /// <summary>
    /// 填充模型下拉框：完全以「获取模型列表」拉取到的列表为准（先清空再添加）；
    /// 从未拉取过时才给出内置候选，方便首次使用。
    /// </summary>
    private void PopulateModelBox(string? select)
    {
        var keep = string.IsNullOrWhiteSpace(select) ? ModelBox.Text : select;

        var models = (App.Settings.Api.KnownModels ?? new List<string>())
            .Select(m => (m ?? "").Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (models.Count == 0) models = BuiltInModels.ToList();

        ModelBox.Items.Clear();
        foreach (var m in models) ModelBox.Items.Add(m);
        if (!string.IsNullOrWhiteSpace(keep)) ModelBox.Text = keep;
    }

    private void AddHotkey_Click(object sender, RoutedEventArgs e) => AddRow(new HotkeyEntry());

    private void AddRow(HotkeyEntry h)
    {
        var row = new HotkeyRow(h);
        row.DeleteRequested += r =>
        {
            HotkeysPanel.Children.Remove(r.Root);
            _rows.Remove(r);
        };
        HotkeysPanel.Children.Add(row.Root);
        _rows.Add(row);
    }

    // ---------- API Key 显示/隐藏 ----------

    private void ShowKey_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowKeyCheck.IsChecked == true)
        {
            ApiKeyText.Text = ApiKeyBox.Password;
            ApiKeyBox.Visibility = Visibility.Collapsed;
            ApiKeyText.Visibility = Visibility.Visible;
        }
        else
        {
            ApiKeyBox.Password = ApiKeyText.Text;
            ApiKeyBox.Visibility = Visibility.Visible;
            ApiKeyText.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- 获取模型列表 ----------

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            SetModelStatus("请先填写接口地址 (Base URL)。", warn: true);
            BaseUrlBox.Focus();
            return;
        }

        _modelCts?.Cancel();
        _modelCts = new CancellationTokenSource();
        var ct = _modelCts.Token;

        FetchModelsBtn.IsEnabled = false;
        var oldContent = FetchModelsBtn.Content;
        FetchModelsBtn.Content = "获取中…";
        SetModelStatus("正在从接口获取模型列表…");

        try
        {
            var cfg = new ApiSettings
            {
                BaseUrl = baseUrl,
                ApiKey = ApiKeyBox.Password,
                Model = ModelBox.Text.Trim(),
                TimeoutSeconds = int.TryParse(TimeoutBox.Text, out var t) ? Math.Clamp(t, 5, 600) : 60,
            };

            var models = await new OpenAIClient(cfg).ListModelsAsync(ct);
            if (ct.IsCancellationRequested) return;

            // 缓存模型列表，下次打开首选项即可直接下拉选择（保存设置时一并写入）
            App.Settings.Api.KnownModels = models;
            try { App.SettingsService.Save(App.Settings); } catch (Exception ex) { Log.Error(ex); }

            // 清空旧列表再填入本次拉取结果，避免与内置候选/上次的模型混在一起
            PopulateModelBox(null);
            SetModelStatus($"获取成功：共 {models.Count} 个模型，已替换下拉列表，可直接选择或输入。", ok: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetModelStatus("获取失败：" + ex.Message, warn: true);
            Log.Error(ex);
        }
        finally
        {
            FetchModelsBtn.Content = oldContent;
            FetchModelsBtn.IsEnabled = true;
        }
    }

    private void SetModelStatus(string text, bool warn = false, bool ok = false)
    {
        ModelStatus.Text = text;
        var key = warn ? "WarnTextBrush" : ok ? "OkTextBrush" : "SubtleTextBrush";
        if (TryFindResource(key) is Brush b) ModelStatus.Foreground = b;
    }

    // ---------- 测试连接 ----------

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestBtn.IsEnabled = false;
        TestStatus.Text = "测试中…";
        try
        {
            var cfg = new ApiSettings
            {
                BaseUrl = BaseUrlBox.Text.Trim(),
                ApiKey = ApiKeyBox.Password,
                Model = ModelBox.Text.Trim(),
                TimeoutSeconds = int.TryParse(TimeoutBox.Text, out var t) ? Math.Clamp(t, 5, 600) : 60,
            };
            var client = new OpenAIClient(cfg);
            TestStatus.Text = await client.TestAsync();
        }
        catch (Exception ex)
        {
            TestStatus.Text = "失败：" + ex.Message;
        }
        finally { TestBtn.IsEnabled = true; }
    }

    // ---------- 保存 ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.Api.BaseUrl = BaseUrlBox.Text.Trim();
        s.Api.ApiKey = ApiKeyBox.Password;
        s.Api.Model = ModelBox.Text.Trim();
        s.Api.Temperature = TempSlider.Value;
        s.Api.MaxTokens = int.TryParse(MaxTokensBox.Text, out var mt) ? Math.Clamp(mt, 1, 32768) : 2048;
        s.Api.TimeoutSeconds = int.TryParse(TimeoutBox.Text, out var to) ? Math.Clamp(to, 5, 600) : 60;
        s.Api.ImageMode = ImgOcrRadio.IsChecked == true ? "Ocr" : "Vision";
        // 记录当前下拉框中的候选模型，便于下次直接选择
        s.Api.KnownModels = ModelBox.Items.OfType<string>().ToList();

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
    private ComboBox? _langBox;

    /// <summary>把可编辑的目标语言下拉框当前文本写回条目（保存时调用）。</summary>
    public void Commit()
    {
        if (_langBox != null) Entry.TargetLanguage = _langBox.Text;
    }

    public HotkeyRow(HotkeyEntry entry)
    {
        Entry = entry;

        var grid = new Grid();
        foreach (var width in new[] { 116d, 104d, 138d, 132d, 78d, 40d, 30d })
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
        actionBox.SelectedIndex = Entry.Action switch { "GenerateReply" => 1, "ScreenshotOcr" => 2, _ => 0 };
        actionBox.SelectionChanged += (_, _) =>
            Entry.Action = actionBox.SelectedIndex switch { 1 => "GenerateReply", 2 => "ScreenshotOcr", _ => "Translate" };
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
        _promptBtn = new Button
        {
            Width = 74,
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _promptBtn.Click += (_, _) => EditPrompt();
        RefreshPromptButton();
        Grid.SetColumn(_promptBtn, 4);
        grid.Children.Add(_promptBtn);

        // 启用
        var enableBox = new CheckBox
        {
            IsChecked = Entry.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        enableBox.Checked += (_, _) => Entry.Enabled = true;
        enableBox.Unchecked += (_, _) => Entry.Enabled = false;
        Grid.SetColumn(enableBox, 5);
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
        Grid.SetColumn(delBtn, 6);
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

    /// <summary>为该热键单独设置系统提示词（留空 = 使用对应功能的默认提示词）。</summary>
    private void EditPrompt()
    {
        var sub = Entry.Action switch
        {
            "GenerateReply" => "只对这条「生成回复」热键生效；留空则使用「提示词」页里“生成回复提示词”的默认内容。",
            "ScreenshotOcr" => "只对这条「截图翻译」热键生效；留空则使用「提示词」页里“截图翻译提示词”的默认内容。",
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

    private static string Shorten(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)    {
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
