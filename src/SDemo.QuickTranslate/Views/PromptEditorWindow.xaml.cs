using System.Windows;

namespace SDemo.QuickTranslate.Views;

/// <summary>通用提示词编辑器（供“每个热键单独设置提示词”使用）。</summary>
public partial class PromptEditorWindow : Window
{
    /// <summary>编辑结果；取消为 null。</summary>
    public string? Result { get; private set; }

    public PromptEditorWindow()
    {
        InitializeComponent();
    }

    /// <summary>弹出提示词编辑器。返回编辑后的文本；取消返回 null。</summary>
    public static string? Edit(Window? owner, string title, string subtitle, string current)
    {
        var w = new PromptEditorWindow { Owner = owner };
        w.HeadText.Text = title;
        w.SubText.Text = subtitle;
        w.PromptBox.Text = current ?? "";
        w.PromptBox.CaretIndex = w.PromptBox.Text.Length;
        w.Loaded += (_, _) => w.PromptBox.Focus();
        return w.ShowDialog() == true ? w.Result : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = PromptBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        // 清空输入框但不关闭，用户可继续编辑或直接确定
        PromptBox.Text = "";
        PromptBox.Focus();
    }
}
