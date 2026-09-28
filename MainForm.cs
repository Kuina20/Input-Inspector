using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace InputInspector;

public sealed class MainForm : Form
{
    private readonly KeyboardHook _hook = new();
    private readonly ConcurrentQueue<RawKeyEvent> _pending = new();
    private readonly List<KeyStrokeRecord> _records = new();
    private readonly Dictionary<uint, string> _processNameCache = new();

    private readonly Button _btnStart = new() { Text = "开始记录", Width = 96 };
    private readonly Button _btnStop = new() { Text = "停止记录", Width = 96 };
    private readonly Button _btnClear = new() { Text = "清空", Width = 72 };
    private readonly Button _btnExport = new() { Text = "导出…", Width = 96 };
    private readonly CheckBox _chkOnlyKeyDown = new()
    {
        Text = "只记录按键按下",
        Checked = true,
        AutoSize = true,
        Margin = new Padding(12, 9, 3, 3),
    };

    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        VirtualMode = true, // 长时间记录可能积累上万条，用虚拟模式避免卡顿
    };

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _lblState = new() { Text = "就绪" };
    private readonly ToolStripStatusLabel _lblCount = new() { Text = "已记录 0 条" };
    private readonly ToolStripStatusLabel _lblDuration = new() { Text = "" };

    private readonly NotifyIcon _trayIcon;
    private bool _allowExit;
    private bool _trayTipShown;

    private readonly System.Windows.Forms.Timer _pumpTimer = new() { Interval = 150 };
    private readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 500 };

    private DateTime _recordStartTime;

    public MainForm()
    {
        Text = "InputInspector - 键盘输入记录器";
        Width = 960;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(6, 5, 6, 0),
            WrapContents = false,
        };
        topPanel.Controls.AddRange([_btnStart, _btnStop, _btnClear, _btnExport, _chkOnlyKeyDown]);

        _list.Columns.Add("时间", 150);
        _list.Columns.Add("事件", 56);
        _list.Columns.Add("按键", 110);
        _list.Columns.Add("字符", 50);
        _list.Columns.Add("VK码", 52);
        _list.Columns.Add("注入", 46);
        _list.Columns.Add("前台进程", 130);
        _list.Columns.Add("前台窗口", 260);

        _status.Items.AddRange([_lblState, new ToolStripSeparator(), _lblCount, new ToolStripSeparator(), _lblDuration]);
        _status.Dock = DockStyle.Bottom;

        Controls.Add(_list);      // Fill 的控件先加
        Controls.Add(_status);
        Controls.Add(topPanel);

        _hook.KeyEvent += (_, e) => _pending.Enqueue(e);
        _btnStart.Click += (_, _) => StartRecording();
        _btnStop.Click += (_, _) => StopRecording();
        _btnClear.Click += (_, _) => ClearRecords();
        _btnExport.Click += (_, _) => ExportRecords();
        _pumpTimer.Tick += (_, _) => PumpPending();
        _clockTimer.Tick += (_, _) => UpdateDuration();
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示主窗口", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "InputInspector",
            ContextMenuStrip = trayMenu,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        UpdateButtons();
    }

    private void StartRecording()
    {
        if (_hook.IsHooked) return;
        try
        {
            _hook.Install();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "无法启动记录", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _recordStartTime = DateTime.Now;
        _pumpTimer.Start();
        _clockTimer.Start();
        _lblState.Text = "● 记录中";
        _lblState.ForeColor = Color.DarkRed;
        UpdateButtons();
    }

    private void StopRecording()
    {
        _hook.Uninstall();
        _pumpTimer.Stop();
        _clockTimer.Stop();
        PumpPending(); // 把队列里剩余的事件处理完
        _lblState.Text = "已停止";
        _lblState.ForeColor = SystemColors.ControlText;
        UpdateButtons();
    }

    private void ClearRecords()
    {
        if (_records.Count > 0 &&
            MessageBox.Show(this, "确定清空所有已记录的按键吗？", "清空记录",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _records.Clear();
        _list.VirtualListSize = 0;
        _lblCount.Text = "已记录 0 条";
        UpdateButtons();
    }

    /// <summary>把钩子回调入队的原始事件富化后追加到记录列表。</summary>
    private void PumpPending()
    {
        bool added = false;
        while (_pending.TryDequeue(out var raw))
        {
            if (_chkOnlyKeyDown.Checked && !raw.IsDown)
                continue;

            _records.Add(Enrich(raw));
            added = true;
        }

        if (added)
        {
            _list.VirtualListSize = _records.Count;
            _list.EnsureVisible(_records.Count - 1);
            _lblCount.Text = $"已记录 {_records.Count} 条";
            UpdateButtons();
        }
    }

    private KeyStrokeRecord Enrich(RawKeyEvent raw)
    {
        string keyName = GetKeyName(raw.VirtualKey, raw.ScanCode, raw.Extended);
        string character = raw.IsDown ? GetCharacter(raw.VirtualKey, raw.ScanCode) : "";
        GetForegroundInfo(out string processName, out string windowTitle);

        return new KeyStrokeRecord(
            raw.Timestamp,
            raw.IsDown ? "按下" : "松开",
            keyName,
            character,
            raw.VirtualKey,
            raw.Injected,
            processName,
            windowTitle);
    }

    private static string GetKeyName(uint vk, uint scanCode, bool extended)
    {
        // GetKeyNameText 返回系统语言的键名（中文系统返回中文）
        int lParam = (int)(scanCode << 16);
        if (extended) lParam |= 1 << 24;
        var sb = new StringBuilder(64);
        if (NativeMethods.GetKeyNameText(lParam, sb, sb.Capacity) > 0)
            return sb.ToString();
        return ((Keys)vk).ToString();
    }

    private static string GetCharacter(uint vk, uint scanCode)
    {
        // 用 GetKeyState 实时拼出修饰键状态，再换算成当前布局下的字符
        var state = new byte[256];
        state[NativeMethods.VK_SHIFT] = KeyStateByte(NativeMethods.VK_SHIFT);
        state[NativeMethods.VK_CONTROL] = KeyStateByte(NativeMethods.VK_CONTROL);
        state[NativeMethods.VK_MENU] = KeyStateByte(NativeMethods.VK_MENU);
        state[NativeMethods.VK_CAPITAL] = KeyStateByte(NativeMethods.VK_CAPITAL);

        var sb = new StringBuilder(8);
        int result = NativeMethods.ToUnicodeEx(vk, scanCode, state, sb, sb.Capacity, 0,
            NativeMethods.GetKeyboardLayout(0));
        if (result <= 0) return "";
        string s = sb.ToString();
        return s.Any(char.IsControl) ? "" : s;
    }

    private static byte KeyStateByte(int vk)
    {
        short s = NativeMethods.GetKeyState(vk);
        byte b = 0;
        if ((s & unchecked((short)0x8000)) != 0) b |= 0x80; // 按下
        if ((s & 0x0001) != 0) b |= 0x01;                   // 切换态（CapsLock）
        return b;
    }

    private void GetForegroundInfo(out string processName, out string windowTitle)
    {
        processName = "";
        windowTitle = "";
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        var sb = new StringBuilder(512);
        if (NativeMethods.GetWindowText(hwnd, sb, sb.Capacity) > 0)
            windowTitle = sb.ToString();

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return;
        if (!_processNameCache.TryGetValue(pid, out string? name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = $"PID {pid}"; }
            _processNameCache[pid] = name;
        }
        processName = name;
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var r = _records[e.ItemIndex];
        e.Item = new ListViewItem([
            r.Time.ToString("HH:mm:ss.fff"),
            r.EventKind,
            r.KeyName,
            r.Character,
            $"0x{r.VirtualKey:X2}",
            r.Injected ? "是" : "",
            r.ProcessName,
            r.WindowTitle,
        ]);
    }

    private void UpdateDuration()
    {
        var elapsed = DateTime.Now - _recordStartTime;
        _lblDuration.Text = $"本次已记录 {elapsed:hh\\:mm\\:ss}";
    }

    private void UpdateButtons()
    {
        bool recording = _hook.IsHooked;
        _btnStart.Enabled = !recording;
        _btnStop.Enabled = recording;
        _btnClear.Enabled = _records.Count > 0;
        _btnExport.Enabled = _records.Count > 0;
    }

    private void ExportRecords()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出按键记录",
            Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt",
            FileName = $"keylog_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // UTF-8 带 BOM，Excel 打开中文不乱码
            using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
            if (dlg.FilterIndex == 1) WriteCsv(writer);
            else WriteText(writer);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        MessageBox.Show(this, $"已导出 {_records.Count} 条记录。", "导出完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void WriteCsv(TextWriter writer)
    {
        writer.WriteLine("时间,事件,按键,字符,VK码,注入,前台进程,前台窗口");
        foreach (var r in _records)
        {
            writer.WriteLine(string.Join(',',
                Csv(r.Time.ToString("yyyy-MM-dd HH:mm:ss.fff")),
                Csv(r.EventKind),
                Csv(r.KeyName),
                Csv(r.Character),
                $"0x{r.VirtualKey:X2}",
                r.Injected ? "是" : "否",
                Csv(r.ProcessName),
                Csv(r.WindowTitle)));
        }

        static string Csv(string s) =>
            s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? $"\"{s.Replace("\"", "\"\"")}\""
                : s;
    }

    private void WriteText(TextWriter writer)
    {
        writer.WriteLine($"InputInspector 按键记录 - 导出于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine(new string('-', 60));
        foreach (var r in _records)
        {
            string ch = string.IsNullOrEmpty(r.Character) ? "" : $" ({r.Character})";
            string inj = r.Injected ? " [注入]" : "";
            writer.WriteLine($"{r.Time:yyyy-MM-dd HH:mm:ss.fff} [{r.EventKind}] {r.KeyName}{ch}{inj}  进程: {r.ProcessName}  窗口: {r.WindowTitle}");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 用户点关闭按钮时最小化到托盘，记录功能不受影响；从托盘菜单选"退出"才真正关闭
        if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        StopRecording();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _hook.Dispose();
        base.OnFormClosing(e);
    }

    private void MinimizeToTray()
    {
        Hide();
        _trayIcon.Visible = true;
        if (!_trayTipShown)
        {
            _trayTipShown = true;
            _trayIcon.ShowBalloonTip(2000, "InputInspector",
                _hook.IsHooked ? "已最小化到托盘，记录仍在继续。" : "已最小化到托盘。",
                ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
    }
}
