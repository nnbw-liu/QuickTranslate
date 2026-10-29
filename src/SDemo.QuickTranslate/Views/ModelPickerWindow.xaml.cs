using System.Windows;
using System.Windows.Controls;
using SDemo.QuickTranslate.Models;
using SDemo.QuickTranslate.Services;

namespace SDemo.QuickTranslate.Views;

/// <summary>
/// 给单条热键指定「API + 模型」的小对话框。
/// 选「跟随默认模型」= 清空该热键的模型，让请求走「默认模型」。
/// </summary>
public partial class ModelPickerWindow : Window
{
    private sealed class ProviderChoice
    {
        public ApiProvider Provider { get; }
        public ProviderChoice(ApiProvider p) => Provider = p;
        public override string ToString() => string.IsNullOrWhiteSpace(Provider.Name) ? "(未命名 API)" : Provider.Name;
    }

    private readonly ModelRef _result = new();
    private bool _loading;

    private ModelPickerWindow()
    {
        InitializeComponent();
        // 可编辑下拉框内部 TextBox 的输入也要实时刷新提示（走 TextChanged 冒泡事件）
        ModelBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new System.Windows.Controls.TextChangedEventHandler((_, _) => { if (!_loading) UpdateModelHint(); }));
    }

    /// <summary>返回 null = 用户取消；返回空引用 = 跟随默认模型；否则是热键专用的 API + 模型。</summary>
    public static ModelRef? Pick(Window? owner, HotkeyEntry entry, string effective)
    {
        var w = new ModelPickerWindow();
        if (owner is { IsLoaded: true }) w.Owner = owner;

        w.HeadText.Text = $"模型 · {(string.IsNullOrWhiteSpace(entry.Name) ? "未命名热键" : entry.Name)}";
        w.SubText.Text = $"只对这条热键生效。优先级：热键模型 > 默认模型。\n{effective}";

        w._loading = true;
        try
        {
            foreach (var p in App.Settings.Providers) w.ApiBox.Items.Add(new ProviderChoice(p));

            w.FollowHint.Text = $"将使用默认模型：{App.Models.Describe(App.Settings.DefaultModel)}" +
                                "\n想给这条热键单独用别的模型？选下面的「单独指定 API 与模型」即可。";

            bool specify = entry.Model?.IsSet == true;
            w.FollowRadio.IsChecked = !specify;
            w.PickRadio.IsChecked = specify;
            w.PickPanel.IsEnabled = specify;

            if (specify)
            {
                for (int i = 0; i < w.ApiBox.Items.Count; i++)
                    if (((ProviderChoice)w.ApiBox.Items[i]).Provider.Id == entry.Model!.ProviderId)
                    {
                        w.ApiBox.SelectedIndex = i;
                        break;
                    }
                w.FillModels(entry.Model!.Model);
            }
            else if (w.ApiBox.Items.Count > 0)
            {
                w.ApiBox.SelectedIndex = 0;
                w.FillModels(null);
            }
        }
        finally { w._loading = false; }

        return w.ShowDialog() == true ? w._result : null;
    }

    private ApiProvider? SelectedProvider => (ApiBox.SelectedItem as ProviderChoice)?.Provider;

    private void FillModels(string? current)
    {
        var p = SelectedProvider;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            ModelBox.Items.Clear();
            foreach (var m in p?.ModelCandidates() ?? new List<string>()) ModelBox.Items.Add(m);
            var text = (current ?? "").Trim();
            if (text.Length > 0 && !ModelBox.Items.Contains(text)) ModelBox.Items.Add(text);
            ModelBox.Text = text.Length > 0 ? text : (p?.DefaultModel ?? "");
            ModelBox.IsEnabled = p != null;
        }
        finally { _loading = wasLoading; }
        UpdateModelHint();
    }

    private void UpdateModelHint()
    {
        if (SelectedProvider == null)
        {
            ModelHint.Text = "还没有 API：请先到 首选项 → 模型 里添加。";
            return;
        }
        var m = ModelBox.Text.Trim();
        ModelHint.Text = m.Length == 0
            ? "请选择或输入模型名（留空将保存失败）。"
            : $"这条热键将使用 {SelectedProvider.Name} / {m}";
    }

    private void ApiBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        FillModels(null);
    }

    private void ModelBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        UpdateModelHint();
    }

    private void FollowRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (PickPanel != null) PickPanel.IsEnabled = false;
    }

    private void PickRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (PickPanel == null) return;
        PickPanel.IsEnabled = true;
        // 直接聚焦模型框：让「可以选模型」这件事一眼可见
        Dispatcher.BeginInvoke(new Action(() => { ModelBox.Focus(); }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (FollowRadio.IsChecked == true)
        {
            _result.Clear();   // 跟随默认模型
            DialogResult = true;
            return;
        }

        var p = SelectedProvider;
        var model = ModelBox.Text.Trim();
        if (p == null || model.Length == 0)
        {
            MessageBox.Show("请选择一个 API 并填写模型名。", "SDemo.QuickTranslate", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _result.ProviderId = p.Id;
        _result.Model = model;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
