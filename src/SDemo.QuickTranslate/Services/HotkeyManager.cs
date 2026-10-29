using System.Windows.Interop;
using SDemo.QuickTranslate.Models;

namespace SDemo.QuickTranslate.Services;

/// <summary>全局热键管理：通过隐藏窗口接收 WM_HOTKEY 消息。</summary>
public sealed class HotkeyManager : IDisposable
{
    public const int WM_HOTKEY = 0x0312;

    private HwndSource? _source;
    private IntPtr _hwnd;
    private readonly Dictionary<int, HotkeyEntry> _byId = new();
    private readonly List<int> _registeredIds = new();

    public event Action<HotkeyEntry>? ActionTriggered;

    public void Init()
    {
        var p = new HwndSourceParameters("SDemo.QuickTranslate.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080 | 0x08000000, // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            PositionX = -32000,
            PositionY = -32000,
        };
        _source = new HwndSource(p);
        _hwnd = _source.Handle;
        _source.AddHook(WndProc);
    }

    /// <summary>注册所有启用的热键，返回错误列表。</summary>
    public List<string> RegisterAll(AppSettings s)
    {
        UnregisterAll();
        var errors = new List<string>();
        int id = 1;
        foreach (var h in s.Hotkeys)
        {
            if (!h.Enabled || h.KeyVk == 0) { id++; continue; }
            uint mod = (uint)(h.Modifiers & 0x0F);
            if (Native.NativeWin.RegisterHotKey(_hwnd, id, mod, (uint)h.KeyVk))
            {
                _byId[id] = h;
                _registeredIds.Add(id);
            }
            else
            {
                errors.Add($"“{h.Name}”({h.Display}) 注册失败：可能已被其它程序占用。");
            }
            id++;
        }
        return errors;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registeredIds)
            Native.NativeWin.UnregisterHotKey(_hwnd, id);
        _registeredIds.Clear();
        _byId.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _byId.TryGetValue((int)wParam, out var entry))
        {
            handled = true;
            ActionTriggered?.Invoke(entry);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
