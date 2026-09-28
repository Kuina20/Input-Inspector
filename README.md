# InputInspector - 键盘输入记录器

基于 C# / .NET 8 / WinForms 的轻量级键盘输入记录工具。使用 Windows 全局低级键盘钩子（`WH_KEYBOARD_LL`）捕获按键，**远程桌面（RDP）会话输入和 TeamViewer / ToDesk / 向日葵等远程控制软件注入的输入同样有效**。

> ⚠️ 请仅在本人设备或获得明确授权的环境中使用本工具。

## 功能

- 开始 / 停止 / 清空记录，实时显示按键流
- 每条记录包含：时间（毫秒精度）、按下/松开、键名（系统语言）、换算字符、VK 码、是否为注入输入、前台进程名、前台窗口标题
- 可勾选"只记录按键按下"，过滤松开事件
- 导出为 CSV（UTF-8 带 BOM，Excel 直接打开不乱码）或纯文本
- ListView 虚拟模式，长时间记录上万条不卡顿

## 为什么在远程控制下依然有效

- 远控软件（TeamViewer、向日葵等）通过 `SendInput` 把按键注入到目标会话，`WH_KEYBOARD_LL` 钩子能正常捕获，且记录的"注入"列会标记为"是"。
- RDP 远程桌面的键盘输入由终端服务驱动投递到会话内，同样在钩子可见范围内。
- 注意：程序必须运行在**你想记录的同一个登录会话**中（例如通过 RDP 登录后，在远程会话里启动本程序）。

## 已知限制（Windows 系统级，任何同类工具都一样）

- 安全桌面（UAC 授权弹窗、Ctrl+Alt+Del、锁屏界面）上的按键无法捕获。
- 若需要完整捕获发往**以管理员身份运行**的窗口的按键，建议本程序也以管理员身份运行。

## 构建与发布

在装有 .NET 8 SDK 的 Windows 机器上：

```bash
dotnet run -c Release
```

发布为单文件 exe（免安装 .NET 运行时）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

产物在 `publish\InputInspector.exe`（约 60~70 MB 自包含；若目标机器已装 .NET 8 桌面运行时，可去掉 `--self-contained`，体积约 1 MB）。
