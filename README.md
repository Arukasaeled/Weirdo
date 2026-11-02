# Kooky for Windows

独立的 Windows 终端工作台，移植自 [Kooky](https://github.com/iAmCorey/kooky) 的产品交互与部分 MIT 源码。使用原生 Windows 宿主、ConPTY、WebView2 与 xterm.js，直接运行官方 CLI。

## 运行与安装

安装 `dist/Kooky-0.1.5-win-x64.msi`，或运行便携目录中的 `dist/app/Kooky.exe`。无需安装 .NET；需要 Windows 10/11 x64 和 Microsoft Edge WebView2 Runtime。安装程序按当前用户安装到 `%LOCALAPPDATA%\Programs\Kooky`。

点击“打开项目”，再选择 PowerShell、Claude Code、Codex 或 Gemini。Agent CLI 需要预先安装并配置；Kooky 继承启动环境，不代理模型请求，不重写 CLI 的权限机制。内置 Codex 使用 `--no-daemon`，避免与 Codex Desktop 的共享服务冲突；需要支持此参数的官方版本（已验证 0.162.0），旧版本可在 Agent 设置中删除该参数。

所有 Agent 端点、模型及凭据仍由官方 CLI 处理。“设置 → Agent”可以编辑命令、自定义环境变量、集成类型和恢复参数。显式配置的环境变量以 Windows DPAPI 加密保存，仅当前 Windows 用户可解密。

原生 EXE 及其参数直接传给 Windows；需要管道、重定向或 shell 表达式时，显式使用 `powershell.exe -Command "…"` 或 `cmd.exe /c "…"`。终端默认报告 `TERM=xterm-256color` 与 `COLORTERM=truecolor`，显式 Agent 环境变量仍可覆盖。

## 使用

- 左侧工作区及垂直会话列表；每个分屏拥有自己的 Tab 栏。
- 拖动 Tab 可以重新排序、移动到其他 Pane 或项目；拖动分隔线调整比例。
- 文件树支持展开、插入带引号的路径、复制路径和系统打开。
- 深色/浅色、终端字体大小、原生会话历史、独立活动监视器。
- Git 显示真实分支、文件数、增删行；超时或超出读取上限会标明部分统计。
- 活动仅标记实际进程存活、终端输出和退出码；不会将普通输出误报为审批请求。

| 快捷键 | 功能 |
| --- | --- |
| Ctrl P | 搜索工作区、会话、Agent 和最近目录 |
| Ctrl Shift O | 打开目录 |
| Ctrl Shift T | 新终端 |
| Ctrl Shift D / Ctrl Alt D | 向右 / 向下分屏 |
| Ctrl Shift E | 最大化当前 Pane |
| Ctrl Shift W | 关闭会话（运行中需确认） |
| Ctrl Tab | 切换 Tab |
| Ctrl Shift C / Ctrl Shift V | 复制选区 / 粘贴 |
| Ctrl F | 搜索终端内容 |
| Ctrl Alt K | 全局显示/隐藏工作台 |
| Ctrl Alt Shift K | 全局显示活动监视器 |

关闭主窗口会隐藏到系统托盘，终端继续运行。托盘的“Close interface, keep terminals”退出界面但保留独立 Broker；重新启动 Kooky 会重新连接原进程。只有“Quit and stop all terminals”才会结束全部任务。

操作系统重启、完整退出或 Broker 崩溃后，进程本身无法继续存活；布局和原生会话 ID 保存在磁盘，可恢复官方 Agent 对话，Shell 则重新启动。Claude/Gemini 在启动时取得明确 UUID；Codex 使用每次启动独有的元数据标记绑定官方 UUID，同目录并行也不会按“最近会话”猜测。

每个终端保留最多约 2 MiB 原始输出用于界面重连，xterm 保留 5000 行滚动历史。回放被截断时不会伪造缺失输出。完整退出后不将终端文本写入磁盘。

## 更新与数据

设置中可配置可信 HTTPS 或本地 JSON 更新清单。配置后启动时及每 6 小时自动检查，新版本通过系统通知提示，也可手动检查。当前独立版本没有公共发布源；未配置时不会声称有更新。更新先下载并校验 SHA-256，要求停止正在运行的终端，然后关闭宿主与 Broker，等待文件锁释放再运行安装程序。

```json
{"version":"0.2.0","url":"https://your-release-host/Kooky-0.2.0-win-x64.msi","sha256":"64位十六进制哈希","notes":"版本说明"}
```

应用数据在 `%LOCALAPPDATA%\Kooky`。版本化 JSON 通过原子替换保存并保留 `.bak`；损坏文件被隔离为 `.corrupt-*`，不会直接覆盖。旧版普通 JSON 自动迁移；来自更新版本的数据拒绝降级覆盖。`errors.log` 记录宿主异常，不记录终端正文或环境变量值。

公开安装包缓存放在 `%PROGRAMDATA%\Kooky\Updates` 的用户专属目录，仅当前用户、SYSTEM 和管理员可访问，避免 Windows Installer 无法读取 EFS 加密包。应用配置仍保持原有加密；安装日志保存在该安装缓存中，失败会在下次启动时提示。

安装包尚未做商业代码签名。原有 Claude/Codex 设置文件由 CLI 自己读取；Kooky 不修复或覆盖这些文件。CLI 自己保存的信任决定、登录状态及对话属于其原生行为。

## 从源码构建

需要 .NET SDK 10、Node.js 22+、PowerShell 7。仅在本项目目录执行：

```powershell
pwsh -File scripts/build.ps1
```

脚本构建前端、运行真实 ConPTY/服务测试、发布自包含宿主并用固定版本 WiX 生成 MSI。首次构建需要下载 NuGet/npm/WiX 依赖。

```powershell
dotnet run --project tests/Kooky.ConPty.Tests
dotnet run --project tests/Kooky.Services.Tests
dotnet build src/Kooky.App
dotnet run --project tests/Kooky.Broker.Tests
```

开发验收可使用 `Kooky.exe --devtools --data-dir <隔离目录>`，仅此模式开启 WebView2 CDP 9334。正常运行不开放调试端口。`scripts/rpc.ps1` 是当前用户命名管道调试客户端，不监听网络端口。

源码分为 `src/Kooky.Core`（ConPTY、配置、Git、原生会话）、`src/Kooky.App`（桌面、Broker、更新）、`web`（真实终端 UI）。上游只读参考保存在 gitignored 的 `reference/kooky`。MIT、上游版权及第三方完整许可见 `LICENSE`、`NOTICE.md`、`licenses/`。
