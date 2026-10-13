using System.Drawing;
using System.Windows.Forms;
using QuickTranslate.Models;

namespace QuickTranslate.Services;

/// <summary>托盘图标与右键菜单。</summary>
public sealed class TrayIconService : IDisposable
{
    private NotifyIcon? _icon;
    private ContextMenuStrip? _menu;

    public event Action<string>? MenuAction;

    public void Init()
    {
        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "QuickTranslate 翻译助手",
            Visible = true,
        };
        RebuildMenu(null);
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => MenuAction?.Invoke("prefs");
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _menu?.Show(Cursor.Position); };
    }

    /// <summary>根据最新设置重建右键菜单。</summary>
    public void RebuildMenu(AppSettings? s)
    {
        var entries = (s?.Hotkeys ?? new List<HotkeyEntry>())
            .Where(h => h.Action == "Translate" && h.Enabled)
            .ToList();

        _menu = new ContextMenuStrip { ShowImageMargin = false };

        if (entries.Count > 0)
        {
            foreach (var h in entries)
            {
                var mi = new ToolStripMenuItem($"{h.Name}  ({h.Display})");
                mi.Click += (_, _) => MenuAction?.Invoke($"translate:{h.TargetLanguage}");
                _menu.Items.Add(mi);
            }
        }
        else
        {
            _menu.Items.Add("翻译为中文", null, (_, _) => MenuAction?.Invoke("translate:中文"));
        }

        _menu.Items.Add("截图翻译", null, (_, _) => MenuAction?.Invoke("ocr"));
        _menu.Items.Add("生成回复", null, (_, _) => MenuAction?.Invoke("reply"));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("首选项…", null, (_, _) => MenuAction?.Invoke("prefs"));
        _menu.Items.Add("帮助", null, (_, _) => MenuAction?.Invoke("help"));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("重启", null, (_, _) => MenuAction?.Invoke("restart"));
        _menu.Items.Add("退出", null, (_, _) => MenuAction?.Invoke("exit"));

        if (_icon != null) _icon.ContextMenuStrip = _menu;
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        try { _icon?.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info); }
        catch { }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (sri != null) { using var s = sri.Stream; return new Icon(s); }
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; }
        _menu?.Dispose();
        _menu = null;
    }
}
