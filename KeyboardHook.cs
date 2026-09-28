using System.Runtime.InteropServices;

namespace InputInspector;

/// <summary>
/// 钩子回调里拿到的原始按键数据。回调必须尽快返回（Windows 对低级钩子有超时限制，
/// 超时会静默摘除钩子），所以这里只做结构化入队，富化处理放到 UI 线程批量做。
/// </summary>
internal readonly struct RawKeyEvent
{
    public required DateTime Timestamp { get; init; }
    public required uint VirtualKey { get; init; }
    public required uint ScanCode { get; init; }
    public required bool IsDown { get; init; }
    public required bool Extended { get; init; }
    public required bool Injected { get; init; }
}

/// <summary>
/// 全局低级键盘钩子（WH_KEYBOARD_LL）。
/// 该钩子工作在当前登录会话内：本机键盘输入、远程桌面（RDP）输入、
/// 以及 TeamViewer/ToDesk/向日葵等远控软件注入的输入（Injected 标记为 true）都能捕获。
/// 必须在带消息循环的线程上安装/卸载（本程序即 UI 线程）。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    // 防止委托被 GC 回收
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;

    public event EventHandler<RawKeyEvent>? KeyEvent;

    public bool IsHooked => _hookId != IntPtr.Zero;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (IsHooked) return;

        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(module.ModuleName),
            0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"SetWindowsHookEx 失败，Win32 错误码：{error}");
        }
    }

    public void Uninstall()
    {
        if (!IsHooked) return;
        NativeMethods.UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            bool isUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                KeyEvent?.Invoke(this, new RawKeyEvent
                {
                    Timestamp = DateTime.Now,
                    VirtualKey = data.vkCode,
                    ScanCode = data.scanCode,
                    IsDown = isDown,
                    Extended = (data.flags & NativeMethods.LLKHF_EXTENDED) != 0,
                    Injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0,
                });
            }
        }
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
