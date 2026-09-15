# Codex Quota Widget

一个很小的 Windows Codex quota 浮窗：无边框、置顶、可拖动，显示 5-hour 与 weekly 剩余额度和重置倒计时。

适用于 Windows 10/11，需要本机已有 Codex Desktop 或 Codex CLI，以及 .NET Framework 4.8（Windows 10/11 通常已包含）。

## 直接运行

1. 先确认 Codex Desktop 或 Codex CLI 已登录。
2. 双击 `dist\CodexQuotaWidget.exe`。
3. 拖动浮窗到喜欢的位置；位置会自动保存。

顶部的 `↻` 可立即刷新，`×` 退出。右键菜单还可复制当前 quota、切换置顶或退出。应用每 60 秒自动刷新。

## 数据与隐私

- 不需要 OpenAI API key。
- 不读取、复制或保存登录凭证。
- 应用启动本机已安装的 `codex app-server --stdio`，初始化后调用 `account/rateLimits/read`。
- 只在本机内存中保留 quota 百分比与重置时间。
- 只在 `%LOCALAPPDATA%\CodexQuotaWidget\settings.ini` 保存窗口位置与置顶选项。

`account/rateLimits/read` 是随本机 Codex 版本提供的 app-server 协议。项目会在运行时探测；如果旧版本没有该方法，浮窗会提示更新 Codex。Codex 升级后若协议变化，也可能需要更新本项目。

## 从源码构建

项目没有第三方依赖。Windows 自带的 .NET Framework 4.x 编译器即可构建：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

输出文件为 `dist\CodexQuotaWidget.exe`。

也可以用 Visual Studio 打开 `CodexQuotaWidget.csproj`，选择 Release 构建。

## 测试 UI

在没有 Codex 登录态的开发环境中，可以显示演示数据：

```powershell
.\dist\CodexQuotaWidget.exe --demo
```

## 常见提示

- **Sign in to Codex first**：先打开 Codex Desktop/CLI 并完成登录，再点刷新。
- **Codex not found**：确认 `codex.exe` 在 `PATH`，或已安装 Codex Desktop。
- **Update Codex to view quota**：当前 Codex 版本没有此本地方法，请先升级 Codex。

## 资源占用设计

这是原生 WinForms 单进程 UI，除了随应用启动的 Codex app-server 子进程，没有 Electron、Node、WebView 或额外运行时。app-server 会保持连接，避免每分钟反复启动进程。
