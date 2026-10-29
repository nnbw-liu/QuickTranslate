using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SDemo.QuickTranslate.Models;
using SDemo.QuickTranslate.Services;

namespace SDemo.QuickTranslate.Views;

public partial class PreferencesWindow : Window
{
    private readonly List<HotkeyRow> _rows = new();

    /// <summary>正在把设置写进控件（或控件回写设置）时为 true，避免控件事件彼此覆盖。</summary>
    private bool _loading;

    private sealed record SectionInfo(string Title, string Summary);

    private static readonly Dictionary<string, SectionInfo> Sections = new()
    {
        ["hotkeys"] = new("热键",
            "全局热键：按一下就能翻译选中内容、生成回复、截图翻译或输入翻译。每条热键都可以单独指定语言、提示词和模型。"),
        ["models"] = new("模型",
            "配置 OpenAI 兼容的模型提供商，并选择所有请求默认使用的模型；单条热键也可以单独指定模型。"),
        ["general"] = new("通用",
            "开机启动、翻译时是否关闭思考，以及译文浮窗出现的位置。"),
        ["prompts"] = new("提示词",
            "翻译、生成回复、截图翻译的默认系统提示词；单条热键设置的提示词会覆盖这里。"),
    };

    public PreferencesWindow() : this(null) { }

    /// <param name="initialSection">"models" / "general" / "prompts" = 直接打开对应分区（调试/截图用）。</param>
    public PreferencesWindow(string? initialSection)
    {
        InitializeComponent();
        LoadSettingsIntoUi();
        SelectSection(initialSection);
        Closed += (_, _) => App.ResumeHotkeys();   // 关闭后恢复全局热键
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

    // ---------- 分区导航 ----------

    private void SelectSection(string? section)
    {
        var name = section?.Trim().ToLowerInvariant();
        var nav =
            name == "models" ? NavModels :
            name == "general" ? NavGeneral :
            name == "prompts" ? NavPrompts :
            NavHotkeys;
        if (nav.IsChecked != true) nav.IsChecked = true;
        else ShowSection(NavTag(nav));
    }

    private static string NavTag(RadioButton nav) => nav.Tag as string ?? "hotkeys";

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton nav) ShowSection(NavTag(nav));
    }

    private void ShowSection(string section)
    {
        if (!Sections.TryGetValue(section, out var info)) section = "hotkeys";
        if (!Sections.TryGetValue(section, out info)) return;
        var s = Sections[section];

        HotkeysSection.Visibility = section == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        ModelsSection.Visibility = section == "models" ? Visibility.Visible : Visibility.Collapsed;
        GeneralSection.Visibility = section == "general" ? Visibility.Visible : Visibility.Collapsed;
        PromptsSection.Visibility = section == "prompts" ? Visibility.Visible : Visibility.Collapsed;

        SectionTitle.Text = s.Title;
        SectionSummary.Text = s.Summary;
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
            RefreshProviderCards();
            PopulateDefaultModelRow();
            HotkeysPanel.Children.Clear();
            _rows.Clear();
            foreach (var h in s.Hotkeys) AddRow(h);
            if (_rows.Count == 0) AddRow(new HotkeyEntry());
        }
        finally { _loading = false; }
    }

    // ---------- 模型提供商（卡片列表） ----------

    private void RefreshProviderCards()
    {
        ProviderCards.ItemsSource = App.Settings.Providers.Select(p => new ProviderCardItem(p)).ToList();
        ProviderEmpty.Visibility = App.Settings.Providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        var added = ProviderEditorWindow.Edit(this, null, NextProviderName());
        if (added == null) return;

        App.Settings.Providers.Add(added);
        RefreshProviderCards();
        PopulateDefaultModelRow();
        EnsureDefaultModel();
    }

    private void EditProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProviderCardItem item }) return;
        var p = item.Provider;

        var edited = ProviderEditorWindow.Edit(this, p);
        if (edited == null) return;

        // 就地替换（Id 保持不变，默认模型 / 热键对该提供商的引用不会断）
        int idx = App.Settings.Providers.IndexOf(p);
        if (idx >= 0) App.Settings.Providers[idx] = edited;
        else App.Settings.Providers.Add(edited);

        // 模型被删 / 改名后，把指向旧模型的地方改到这个提供商的可用模型
        FixModelReferences(edited);
        RefreshProviderCards();
        PopulateDefaultModelRow();
        EnsureDefaultModel();
        RefreshHotkeyModelButtons();
    }

    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProviderCardItem item }) return;
        var p = item.Provider;

        int refs = CountReferences(p.Id);
        var msg = $"确定移除模型提供商「{p.Name}」吗？" +
                  (refs > 0
                      ? $"\n\n默认模型 / 热键模型 里共有 {refs} 处引用了它，移除后这些引用会一并清空（改用下一级，通常就是默认模型）。"
                      : "");
        if (MessageBox.Show(msg, "SDemo.QuickTranslate", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            != MessageBoxResult.OK)
            return;

        App.Settings.Providers.Remove(p);
        ClearReferences(p.Id);
        RefreshProviderCards();
        PopulateDefaultModelRow();
        EnsureDefaultModel();
        RefreshHotkeyModelButtons();
    }

    /// <summary>提供商的模型变化后，修正默认模型 / 热键里已不存在的模型名。</summary>
    private static void FixModelReferences(ApiProvider p)
    {
        var candidates = p.ModelCandidates();
        string Fix(string model)
            => candidates.Contains(model, StringComparer.OrdinalIgnoreCase)
                ? model
                : (candidates.FirstOrDefault() ?? "");

        if (App.Settings.DefaultModel.ProviderId == p.Id)
            App.Settings.DefaultModel.Model = Fix(App.Settings.DefaultModel.Model);
        foreach (var h in App.Settings.Hotkeys)
            if (h.Model != null && h.Model.ProviderId == p.Id)
                h.Model.Model = Fix(h.Model.Model);
    }

    private static void EnsureDefaultModel()
    {
        if (App.Settings.DefaultModel.IsSet) return;
        var p = App.Settings.Providers.FirstOrDefault(x => x.ModelCandidates().Count > 0)
                ?? App.Settings.Providers.FirstOrDefault();
        if (p == null) return;
        App.Settings.DefaultModel.ProviderId = p.Id;
        App.Settings.DefaultModel.Model = p.DefaultModel.Length > 0
            ? p.DefaultModel
            : (p.ModelCandidates().FirstOrDefault() ?? "");
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

    // ---------- 默认模型 ----------

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
            DefaultModelHint.Text = "还没有提供商：请点下方「＋ 添加模型提供商」。";
            return;
        }
        var m = DefaultModelBox.Text.Trim();
        DefaultModelHint.Text = m.Length == 0
            ? "请选择或输入模型名"
            : $"当前生效：{p.Name} / {m}";
    }

    private void DefaultApi_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var p = (DefaultApiBox.SelectedItem as ProviderItem)?.Provider;
        App.Settings.DefaultModel.ProviderId = p?.Id ?? "";
        // 换了提供商，模型也要跟着换成新提供商下的（优先它的常用模型）
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

        // 默认模型编辑框里的文字（模型页编辑即写回，这里只做兜底）
        s.DefaultModel.Model = DefaultModelBox.Text.Trim();
        foreach (var r in _rows) r.Commit();

        if (s.Providers.Count == 0)
        {
            MessageBox.Show("请至少添加一个模型提供商（首选项 → 模型）。", "SDemo.QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SelectSection("models");
            return;
        }
        if (!s.DefaultModel.IsSet)
        {
            MessageBox.Show("请设置「默认模型」（首选项 → 模型）：选择提供商与模型后再保存。", "SDemo.QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SelectSection("models");
            return;
        }
        if (s.Providers.Any(p => string.IsNullOrWhiteSpace(p.BaseUrl)))
        {
            MessageBox.Show("有一个模型提供商没填接口地址，请补全后再保存。", "SDemo.QuickTranslate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SelectSection("models");
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
                MessageBox.Show($"热键“{r.Entry.Name}”未设置按键。", "SDemo.QuickTranslate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var key = (r.Entry.Modifiers, r.Entry.KeyVk);
            if (seen.Contains(key))
            {
                MessageBox.Show($"热键 {r.Entry.Display} 重复，请修改。", "SDemo.QuickTranslate", MessageBoxButton.OK, MessageBoxImage.Warning);
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

/// <summary>模型提供商卡片的数据（只用于绑定显示）。</summary>
internal sealed class ProviderCardItem
{
    public ApiProvider Provider { get; }
    public ProviderCardItem(ApiProvider provider) => Provider = provider;

    public string Name => string.IsNullOrWhiteSpace(Provider.Name) ? "(未命名提供商)" : Provider.Name;

    public string ShortId => Provider.Id.Length <= 8 ? Provider.Id : Provider.Id[..8];

    public string BaseUrl => string.IsNullOrWhiteSpace(Provider.BaseUrl) ? "（未填写接口地址）" : Provider.BaseUrl;

    public string ModelsText
    {
        get
        {
            var candidates = Provider.ModelCandidates();
            if (candidates.Count == 0) return "还没有模型，点「编辑」添加";
            var head = string.Join("、", candidates.Take(3));
            var more = candidates.Count > 3 ? "…" : "";
            return $"{candidates.Count} 个模型 · {head}{more}";
        }
    }
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
