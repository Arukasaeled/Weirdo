<p align="center">
  <img src="web/public/branding/weirdo-logo.png" width="112" alt="Weirdo logo">
</p>

# Weirdo

面向 Windows 的多 Agent 终端工作台。把项目、官方 CLI 和并行会话放在一个紧凑的桌面界面中。

[下载 Windows 安装包](https://github.com/Arukasaeled/Weirdo/releases/latest) · [更新记录](CHANGELOG.md) · [验证记录](VALIDATION.md) · [性能测量](PERFORMANCE.md)

## 日常使用

- **项目管理**支持固定、最近打开、搜索和粘贴目录路径。目录校验完成后立即显示工作区，Git 与运行环境信息在后台加载。
- **原生终端**通过 Windows ConPTY 直接运行 Claude Code、Codex、Gemini CLI、PowerShell 和自定义命令，保留各 CLI 的原生界面、工具与权限流程。
- **并行会话**支持垂直会话列表、独立 Tab、水平和垂直分屏、拖拽、重命名及后台运行。
- **文件树**记住各项目的展开、选择和滚动位置。支持键盘导航、复制路径和向当前终端插入引用后的路径。
- **桌面能力**包括深浅主题、集成标题栏、系统托盘、原生通知、全局快捷键和独立 Agent 活动监视器。
- **环境与状态**显示实际 Git 分支、变更文件、增删行、工作目录及 Node.js/Python 信息，支持自定义 Agent 环境变量和原生 Session ID 恢复。

## 安装

从 [v0.2.1 Release](https://github.com/Arukasaeled/Weirdo/releases/tag/v0.2.1) 下载 `Weirdo-0.2.1-win-x64-setup.exe`，运行后安装到当前用户的 `%LOCALAPPDATA%\Programs\Weirdo`。

支持 Windows 10/11 x64。安装程序会检测系统 WebView2 Runtime，并在缺失时下载。运行 Weirdo 无需另外安装 .NET、Node.js 或 Rust。各 Agent 官方 CLI 需要预先安装并完成自己的登录或端点配置。

安装包约 **4.43 MiB**，尚未签名。Release 同时提供 `SHA256SUMS.txt`，可使用 PowerShell 核对下载文件。

```powershell
Get-FileHash .\Weirdo-0.2.1-win-x64-setup.exe -Algorithm SHA256
```

仓库和 Release 均为私有，下载时需要登录拥有访问权限的 GitHub 账号。

## 开始工作

1. 点击“打开项目”，选择最近目录、搜索项目或粘贴完整路径。也可通过“浏览文件夹”打开系统目录选择器。
2. 在项目中启动 PowerShell 或 Agent。使用 Tab 和分屏管理多个任务。
3. 在文件树中选择文件，按 Enter 或点击插入按钮，将路径放入当前终端。路径会等待你继续编辑或执行。
4. 关闭主窗口后，任务继续运行。通过托盘或全局快捷键重新打开。

托盘的“关闭界面，保留终端”只退出界面。“停止全部终端并退出”会结束任务。系统重启或后台服务退出后，Shell 需要重新启动，Agent 对话可通过保存的原生 Session ID 恢复。

## Agent 配置

在“设置 → Agent”中编辑命令、模型、环境变量与恢复参数。支持 Magpie、自定义 API Endpoint 和任意模型 ID。显式环境变量通过 Windows DPAPI 加密保存，并传给对应子进程。

Claude 的当前会话可使用原生 `/model` 切换模型。“读取网关模型”会读取已保存端点返回的模型 ID，并可同步 Weirdo 管理的 Claude 原生菜单。实际模型权限和调用结果由 CLI 与网关决定。

原生 EXE 参数直接传给 Windows。需要管道或重定向时，显式使用 PowerShell 的 `-Command` 或 cmd 的 `/c`。内置 Codex 使用 `--no-daemon`，需要支持该参数的 CLI 版本。

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| Ctrl Alt W | 显示或隐藏主窗口 |
| Ctrl Alt Shift W | 打开 Agent 活动监视器 |
| Ctrl P | 搜索项目、会话和 Agent |
| Ctrl Shift O | 打开项目选择器 |
| Ctrl Shift T | 新终端 |
| Ctrl Shift D / Ctrl Alt D | 向右 / 向下分屏 |
| Ctrl Shift E | 最大化当前分屏 |
| Ctrl Shift W | 关闭当前终端 |
| Ctrl Shift C / Ctrl Shift V | 复制 / 粘贴 |
| Ctrl F | 搜索终端内容 |

## 技术与构建

桌面宿主使用 **Tauri 2 / Rust + 系统 WebView2**，前端使用 TypeScript 和 xterm.js。独立的 **C# NativeAOT** 后台服务管理 ConPTY 与子进程，界面重新打开时可以连接原有终端。

构建需要 Node.js 22+、.NET SDK 10、Rust MSVC 工具链、Microsoft C++ Build Tools、Windows SDK 和 PowerShell 7。

```powershell
git clone https://github.com/Arukasaeled/Weirdo.git
cd Weirdo
pwsh -File scripts/build.ps1
```

脚本构建前端、运行检查、编译原生后台和 Tauri 宿主，并生成 NSIS EXE 安装包。产物位于 `dist/`。

| 目录 | 内容 |
| --- | --- |
| `src-tauri/` | Rust 桌面宿主、窗口、托盘与 IPC |
| `src/Weirdo.Broker/` | 独立终端服务、更新与资源采样 |
| `src/Weirdo.Core/` | ConPTY、Agent、配置、Git 与原生会话 |
| `web/` | TypeScript 与 xterm.js 界面 |
| `tests/` | ConPTY、服务与后台集成检查 |
| `scripts/` | 构建、诊断及性能测量 |

0.2.1 已完成 **158 项检查**。测量条件和验证范围见 [VALIDATION.md](VALIDATION.md) 与 [PERFORMANCE.md](PERFORMANCE.md)。

## 数据与更新

配置保存在 `%LOCALAPPDATA%\Weirdo`。版本化 JSON 使用原子写入、备份和损坏恢复。首次迁移会复制先前版本的数据，保留原生会话的工作目录与历史。

设置中可配置可信 HTTPS 或本地 JSON 更新清单。更新会校验 SHA-256，并要求先停止终端。此版本附带的开发清单使用本地文件路径；私有 GitHub Release 通过已登录的浏览器下载。

## 许可与来源

项目使用 MIT 许可。原始版权、上游来源及第三方组件说明保存在 [LICENSE](LICENSE)、[NOTICE.md](NOTICE.md) 和 [licenses/](licenses/)。Weirdo 标识使用项目提供的原始 Logo。
