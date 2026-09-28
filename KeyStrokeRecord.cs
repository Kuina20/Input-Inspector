namespace InputInspector;

/// <summary>一条富化后的按键记录（界面展示和导出的数据单元）。</summary>
internal sealed record KeyStrokeRecord(
    DateTime Time,
    string EventKind,    // 按下 / 松开
    string KeyName,
    string Character,    // 按当前输入法布局换算出的字符（可能为空）
    uint VirtualKey,
    bool Injected,       // 是否为软件注入的输入（远控软件常见）
    string ProcessName,  // 前台窗口进程
    string WindowTitle   // 前台窗口标题
);
