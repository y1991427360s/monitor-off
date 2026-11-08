using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace MonitorOff;

public partial class MainWindow : Window
{
    List<Mon> _mons = new();
    readonly HashSet<string> _selected = new();
    bool _busy;
    bool _remoteOn;
    WinForms.NotifyIcon? _tray;
    readonly DispatcherTimer _keepOffTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    IntPtr _hwnd;
    const int HotkeyId = 0x4D4F;

    // 键鼠钩子（自动恢复）
    IntPtr _mouseHook, _kbdHook;
    Native.HookProc? _mouseProc, _kbdProc;
    int _offTick;
    int _wakeQueued;

    static readonly BrushConverter Bc = new();
    static Brush B(string hex) => (Brush)Bc.ConvertFromString(hex)!;

    public MainWindow()
    {
        InitializeComponent();
        var s = Store.Load();
        ChkKeepOff.IsChecked = s.KeepOff;
        ChkAutoWake.IsChecked = s.AutoWake;
        ChkPreferDetach.IsChecked = s.PreferDetach;
        ChkPreferDetach.Click += (_, _) => { var v = ChkPreferDetach.IsChecked == true; Store.Update(x => x.PreferDetach = v); Draw(); };

        TitleBar.MouseLeftButtonDown += (_, _) => DragMove();
        BtnClose.Click += (_, _) => HideToTray();
        BtnHide.Click += (_, _) => HideToTray();
        BtnRefresh.Click += (_, _) => { SetStatus("正在重新检测…"); _ = RefreshAsync(true); };
        BtnAll.Click += (_, _) => { foreach (var m in _mons) _selected.Add(m.Key); Draw(); };
        BtnNone.Click += (_, _) => { _selected.Clear(); Draw(); };
        BtnOff.Click += (_, _) => { var k = _selected.ToList(); RunOp(() => MonitorService.TurnOff(k), clearSel: true); };
        BtnOn.Click += (_, _) => { var k = _selected.ToList(); RunOp(() => MonitorService.TurnOn(k), clearSel: true); };
        BtnRestore.Click += (_, _) => RestoreAll();
        BtnRemote.Click += (_, _) => { if (_remoteOn) RestoreAll(); else RemoteMode(); };
        ChkKeepOff.Click += (_, _) => { var v = ChkKeepOff.IsChecked == true; Store.Update(x => x.KeepOff = v); };
        ChkAutoWake.Click += (_, _) => { var v = ChkAutoWake.IsChecked == true; Store.Update(x => x.AutoWake = v); _offTick = Environment.TickCount; UpdateHooks(); };

        _keepOffTimer.Tick += (_, _) =>
        {
            if (_busy || ChkKeepOff.IsChecked != true || !MonitorService.AnyOff()) return;
            Task.Run(MonitorService.KeepOffTick);
        };
        _keepOffTimer.Start();
    }

    // ---------------- 启动 / 参数 ----------------
    public void Start(string[] args)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        // MOD_ALT=1 | MOD_CONTROL=2 | MOD_NOREPEAT=0x4000，VK_F12=0x7B
        if (!Native.RegisterHotKey(_hwnd, HotkeyId, 0x4003, 0x7B))
        {
            TxtHotkey.Text = "Ctrl+Alt+F12 被其他程序占用，请用「恢复全部」或托盘菜单";
            TxtHotkey.Foreground = B("#D32F2F");
        }
        // Win11 圆角：DWMWA_WINDOW_CORNER_PREFERENCE(33) = DWMWCP_ROUND(2)
        int round = 2;
        Native.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        Program.Trace("hotkey");
        StartPipeServer();

        bool hasAction = args.Any(a => a is "--off" or "--on" or "--restore" or "--remote" or "--tray");
        // 必须在消息循环里启动，await 才能回到界面线程
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () => { _ = RefreshAsync(false); });
        if (!hasAction) ShowWin();
        // 托盘（要加载 WinForms）等窗口画出来后再建，不拖慢首屏
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            CreateTray();
            Program.Trace("tray");
            if (hasAction) HandleArgs(args);
            UpdateHooks();
        });
    }

    void HandleArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (a)
            {
                case "--show": ShowWin(); break;
                case "--restore": RestoreAll(); break;
                case "--remote": RemoteMode(); break;
                case "--off":
                case "--on":
                    var idx = next.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).ToHashSet();
                    bool off = a == "--off";
                    RunOp(() =>
                    {
                        var keys = MonitorService.Enumerate().Where(m => idx.Contains(m.Index)).Select(m => m.Key).ToList();
                        return off ? MonitorService.TurnOff(keys) : MonitorService.TurnOn(keys);
                    });
                    i++;
                    break;
            }
        }
    }

    void StartPipeServer()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(Program.PipeName, PipeDirection.In, 1);
                    s.WaitForConnection();
                    using var r = new StreamReader(s);
                    var line = r.ReadLine() ?? "";
                    var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Dispatcher.BeginInvoke(() => HandleArgs(args.Length == 0 ? new[] { "--show" } : args));
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "pipe" };
        t.Start();
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0312 && w.ToInt32() == HotkeyId) { RestoreAll(); handled = true; }
        return IntPtr.Zero;
    }

    // ---------------- 窗口 / 托盘 ----------------
    void ShowWin()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    void HideToTray()
    {
        Hide();
        if (MonitorService.AnyOff())
            _tray?.ShowBalloonTip(2500, "分屏关闭显示器", "已收到托盘，保持关闭和 Ctrl+Alt+F12 继续有效", WinForms.ToolTipIcon.None);
    }

    void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip { Font = new System.Drawing.Font("Microsoft YaHei UI", 9f) };
        menu.Items.Add("打开面板", null, (_, _) => ShowWin());
        menu.Items.Add("远程模式：关闭全部实体屏", null, (_, _) => RemoteMode());
        menu.Items.Add("恢复全部显示器", null, (_, _) => RestoreAll());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("恢复全部并退出", null, (_, _) => ExitApp());
        System.Drawing.Icon? icon = null;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _tray = new WinForms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "分屏关闭显示器",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) Dispatcher.BeginInvoke(ShowWin); };
    }

    async void ExitApp()
    {
        if (MonitorService.AnyOff()) await Task.Run(() => MonitorService.TurnOn(Array.Empty<string>(), all: true));
        RemoveHooks();
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Shutdown();
    }

    // ---------------- 操作 ----------------
    void SetStatus(string s) => TxtStatus.Text = s;

    void RunOp(Func<List<string>> op, bool clearSel = false)
    {
        if (_busy) { SetStatus("上一个操作还没完成，请稍等"); return; }
        _busy = true;
        SetButtons(false);
        SetStatus("正在处理…");
        Task.Run(() =>
        {
            Thread.Sleep(250); // 等松开鼠标，避免刚关就被唤醒
            try { return string.Join("；", op()); }
            catch (Exception ex) { return "出错：" + ex.Message; }
        }).ContinueWith(t =>
        {
            _busy = false;
            if (clearSel) _selected.Clear();
            SetStatus(t.Result);
            _offTick = Environment.TickCount;
            SetButtons(true);
            UpdateHooks();
            _ = RefreshAsync(false);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void RestoreAll() => RunOp(() => MonitorService.TurnOn(Array.Empty<string>(), all: true), clearSel: true);

    void RemoteMode() => RunOp(() =>
    {
        var mons = MonitorService.Enumerate();
        MonitorService.ProbeMissing(mons);
        var keys = mons.Where(m => m.Active && m.CanDdc).Select(m => m.Key).ToList();
        if (keys.Count == 0) return new List<string> { "没有找到支持 DDC 的实体显示器" };
        var msgs = MonitorService.TurnOff(keys, forceDetach: true);
        var kept = mons.Where(m => m.Active && !m.CanDdc).Select(m => $"{m.Index} 号 {m.Name}").ToList();
        if (kept.Count > 0) msgs.Add("保留：" + string.Join("、", kept));
        return msgs;
    }, clearSel: true);

    void SetButtons(bool on)
    {
        BtnRemote.IsEnabled = on;
        BtnRestore.IsEnabled = on;
        UpdateButtons(on);
    }

    async Task RefreshAsync(bool probeAll)
    {
        try
        {
            var mons = await Task.Run(() =>
            {
                if (probeAll) Store.Update(s => s.DdcCache.Clear());
                return MonitorService.Enumerate();
            });
            _mons = mons;
            Draw();
            if (mons.Any(m => m.Active && m.Ddc == null))
            {
                await Task.Run(() => MonitorService.ProbeMissing(mons));
                _mons = mons;
                Draw();
            }
            if (probeAll) SetStatus("已重新检测");
        }
        catch (Exception ex) { SetStatus("检测失败：" + ex.Message); }
    }

    void UpdateButtons(bool enabled = true)
    {
        var sel = _mons.Where(m => _selected.Contains(m.Key)).ToList();
        int nOff = sel.Count(m => !m.Off), nOn = sel.Count(m => m.Off);
        BtnOff.IsEnabled = enabled && !_busy && nOff > 0;
        BtnOn.IsEnabled = enabled && !_busy && nOn > 0;
        TxtOff.Text = nOff > 0 ? $"关闭 {nOff} 台" : "关闭所选";
        TxtOn.Text = nOn > 0 ? $"打开 {nOn} 台" : "打开所选";

        // 实体屏 = 探测到支持 DDC 的屏（断开后也记得）
        var phys = _mons.Where(m => m.Physical || m.CanDdc).ToList();
        var keep = _mons.Where(m => !(m.Physical || m.CanDdc)).ToList();
        _remoteOn = phys.Count > 0 && phys.All(m => m.Off) && keep.Any(m => m.Active);
        if (_remoteOn)
        {
            TxtRemote.Text = $"远程模式已开启：{string.Join("、", phys.Select(m => m.Index + " 号"))} 已关闭";
            TxtRemoteSub.Text = "点这里退出远程模式，恢复全部显示器";
        }
        else
        {
            TxtRemote.Text = phys.Count > 0
                ? $"远程模式：关闭 {string.Join("、", phys.Select(m => m.Index + " 号"))}" + (keep.Count > 0 ? $"，只留 {string.Join("、", keep.Select(m => m.Index + " 号"))}" : "")
                : "远程模式：关闭全部实体屏";
            TxtRemoteSub.Text = "断开实体屏的输出（不会自己亮），虚拟屏变主屏，智能体照常运行，ToDesk 照常能看";
        }
    }

    // ---------------- 布局图 ----------------
    void Draw()
    {
        foreach (var k in _selected.ToList()) if (!_mons.Any(m => m.Key == k)) _selected.Remove(k);
        Map.Children.Clear();
        if (_mons.Count == 0) { UpdateButtons(!_busy); return; }

        int minX = _mons.Min(m => m.DX), minY = _mons.Min(m => m.DY);
        int maxX = _mons.Max(m => m.DX + m.DW), maxY = _mons.Max(m => m.DY + m.DH);
        double availW = 470, gap = 8;
        double scale = availW / Math.Max(1, maxX - minX);
        Map.Height = Math.Max(70, (maxY - minY) * scale);

        foreach (var m in _mons)
        {
            bool sel = _selected.Contains(m.Key);
            var b = new Border
            {
                Width = Math.Max(60, m.DW * scale - gap),
                Height = Math.Max(56, m.DH * scale - gap),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(sel ? 2 : 1),
                BorderBrush = B(sel ? "#3B82F6" : "#E4E6EB"),
                Background = B(sel ? (m.Off ? "#E8EEF9" : "#EFF5FF") : (m.Off ? "#ECEEF1" : "#FFFFFF")),
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = m.Index.ToString(), FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = B(m.Off ? "#9CA3AF" : sel ? "#3B82F6" : "#1F2329")
            });
            sp.Children.Add(new TextBlock
            {
                Text = m.Name, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = b.Width - 10, Foreground = B(m.Off ? "#6B7280" : "#1F2329")
            });
            bool detachMode = Store.Load().PreferDetach;
            string how = m.Ddc switch { true => detachMode ? "断开输出" : "DDC", false => m.Active ? "无 DDC" : "已断开", null => "检测中…" };
            sp.Children.Add(new TextBlock
            {
                Text = m.Off ? "● 已关闭" : m.Primary ? $"● 主屏 · {how}" : $"● 开启 · {how}",
                FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = b.Width - 10, Foreground = B(m.Off ? "#D32F2F" : "#16A34A")
            });
            b.Child = sp;

            var btn = new Button { Template = (ControlTemplate)FindResource("TileTpl"), Content = b, Cursor = Cursors.Hand, Tag = m.Key };
            System.Windows.Automation.AutomationProperties.SetName(btn, $"显示器 {m.Index}");
            btn.ToolTip = $"{m.Index} 号：{m.Name}\n{(m.Active ? $"{m.Gdi}  {m.W}×{m.H}" : "未接入桌面")}\n" +
                          (m.Ddc == true && !detachMode ? "关闭方式：DDC/CI 让这台单独待机，桌面布局不变（个别显示器会自己亮）" : "关闭方式：断开这一路输出，显示器无信号后待机（窗口会挪到其他屏）");
            btn.Click += (s, _) =>
            {
                var key = (string)((Button)s).Tag;
                if (!_selected.Remove(key)) _selected.Add(key);
                Draw();
            };
            Canvas.SetLeft(btn, (m.DX - minX) * scale + gap / 2);
            Canvas.SetTop(btn, (m.DY - minY) * scale + gap / 2);
            Map.Children.Add(btn);
        }
        UpdateButtons(!_busy);
    }

    // ---------------- 自动恢复（只认本机物理键鼠） ----------------
    void UpdateHooks()
    {
        bool want = ChkAutoWake.IsChecked == true && MonitorService.AnyOff();
        if (want && _mouseHook == IntPtr.Zero)
        {
            _mouseProc = MouseProc; _kbdProc = KbdProc;
            var mod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(14, _mouseProc, mod, 0);
            _kbdHook = Native.SetWindowsHookEx(13, _kbdProc, mod, 0);
            _wakeQueued = 0;
        }
        else if (!want) RemoveHooks();
    }

    void RemoveHooks()
    {
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_kbdHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kbdHook); _kbdHook = IntPtr.Zero; }
    }

    void PhysicalInput()
    {
        if (Environment.TickCount - _offTick < 2000) return;
        if (Interlocked.Exchange(ref _wakeQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(() => { RemoveHooks(); RestoreAll(); });
    }

    IntPtr MouseProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            uint flags = (uint)Marshal.ReadInt32(l, 12); // MSLLHOOKSTRUCT.flags
            if ((flags & 0x3) == 0) PhysicalInput();     // LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED
        }
        return Native.CallNextHookEx(_mouseHook, code, w, l);
    }

    IntPtr KbdProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            uint flags = (uint)Marshal.ReadInt32(l, 8);  // KBDLLHOOKSTRUCT.flags
            if ((flags & 0x12) == 0) PhysicalInput();    // LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED
        }
        return Native.CallNextHookEx(_kbdHook, code, w, l);
    }
}
