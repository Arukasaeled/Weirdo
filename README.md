# Weirdo

Windows 多 Agent 终端工作台。Tauri 2 / Rust 提供桌面窗口，系统 WebView2 与 xterm.js 渲染终端，独立 NativeAOT 后台服务通过 Windows ConPTY 运行官方 CLI。

## 安装

运行 `dist/Weirdo-0.2.1-win-x64-setup.exe`，按当前用户安装到 `%LOCALAPPDATA%\Programs\Weirdo`。也可直接运行便携目录 `dist/app/Weirdo.exe`，并保留同目录的 `Weirdo.Broker.exe` 与许可证。

支持 Windows 10/11 x64，使用系统 WebView2 Runtime。EXE 安装程序可安装缺失的 WebView2。无需安装 .NET、Node.js 或 Rust 运行 Weirdo。各 Agent 官方 CLI 需预先安装并完成自己的登录或端点配置。

关闭主窗口会隐藏到托盘。托盘的“关闭界面，保留终端”会退出界面并保留独立后台服务。再次启动会连接原有进程。“停止全部终端并退出”会结束所有任务。系统重启后的进程需要重新启动，Agent 可以通过保存的原生 Session ID 恢复对话。

## 模型与代理

在“设置 → Agent → Claude Code → 模型”中选择或输入新会话的默认模型。支持完整的自定义模型 ID。留空会读取环境变量和原生 CLI 设置。已有会话可通过 Claude 原生 `/model` 命令切换。

“读取网关模型”使用该 Agent 已保存的端点和凭据查询模型列表，列出网关实际返回的 ID。对于 Weirdo 管理的 Claude 配置，还会同步原生模型菜单，新启动的 CLI 会读取更新后的菜单。外部或全局 CLI 配置由用户自行管理。列表中出现模型只说明网关返回了这个 ID，实际权限与模型调用结果仍由网关和 CLI 决定。

Claude 自带的网关自动发现只保留含 `claude` 或 `anthropic` 的 ID。Magpie 的其他模型可以通过上述读取操作、输入完整 ID 或原生 modelPicker 配置选择。

显式 Agent 环境变量通过 Windows DPAPI 加密，仅传给对应子进程。Weirdo 沿用已有代理、认证和权限机制。原生 EXE 参数直接交给 Windows，复杂 shell 表达式请显式使用 PowerShell 的 `-Command` 或 cmd 的 `/c`。内置 Codex 使用 `--no-daemon`。

## 工作区与快捷键

左侧管理固定、最近打开的项目与垂直会话列表。每个分屏有独立 Tab，支持拖动、重命名和关闭。文件树可以插入已引用的路径。Git 分支、变更文件和增删行数从实际工作目录读取。

“打开项目”中可搜索固定、已打开和最近项目，也可粘贴完整目录路径后按 Enter。支持带引号的路径、`%USERPROFILE%` 和 `~`。浏览文件夹时从当前项目开始。目录通过校验后立即显示，Git 与环境状态随后补齐。

文件树会保留每个工作区的展开目录、选中项和滚动位置。方向键用于浏览，文件上的 Enter 或插入按钮会将路径放入当前终端，等待你继续编辑或执行。复制按钮和 Ctrl C 可复制路径，双击文件使用 Windows 打开。刷新按钮读取目录的最新内容并保留展开状态。

| 快捷键 | 操作 |
| --- | --- |
| Ctrl Alt W | 全局显示或隐藏主窗口 |
| Ctrl Alt Shift W | Agent 活动监视器 |
| Ctrl P | 搜索项目、会话和 Agent |
| Ctrl Shift O | 打开项目 |
| Ctrl Shift T | 新终端 |
| Ctrl Shift D / Ctrl Alt D | 向右或向下分屏 |
| Ctrl Shift E | 最大化当前分屏 |
| Ctrl Shift W | 关闭当前终端 |
| Ctrl Shift C / Ctrl Shift V | 复制与粘贴 |
| Ctrl F | 搜索终端 |

标题栏与工具栏合并，支持深浅主题、拖动、双击最大化以及最小化、还原和关闭。活动状态依据真实进程与输出，不从普通文本猜测权限审批。

## 数据与升级

应用数据位于 `%LOCALAPPDATA%\Weirdo`。首次正常启动会复制先前版本的工作区、Agent 配置和原生会话记录，兼容旧加密格式，并保留源数据。迁移保持历史会话原来的工作目录。显式指定其他 `--data-dir` 的隔离配置不会自动导入旧数据。

JSON 配置使用原子写入和备份，损坏文件会隔离。已有 EFS 加密属性继续保留。旧产品名仅用于源代码版权声明、旧数据兼容读取及其回归测试。

设置中可填写可信 HTTPS 或本地 JSON 更新清单。更新下载后核对 SHA-256，并在所有终端停止后退出界面和后台服务再安装。当前提供本地更新清单 `dist/update-local.json`，尚未设置公共发布服务。安装包尚未签署商业代码签名。

## 构建

需要 Node.js 22+、.NET SDK 10、Rust MSVC 工具链和 Microsoft C++ Build Tools / Windows SDK。构建工具只在开发阶段使用。

```powershell
pwsh -File scripts/build.ps1
```

脚本构建前端、运行回归检查、将后台服务编译为 NativeAOT EXE、编译 Tauri 2 宿主，并使用 Tauri 的 NSIS 打包器生成 EXE 安装包。

- `src-tauri` 包含 Rust 桌面宿主、窗口、托盘及 IPC。
- `src/Weirdo.Broker` 包含独立终端服务、更新和资源采样。
- `src/Weirdo.Core` 包含 ConPTY、Agent、配置、Git 和原生会话。
- `web` 包含 TypeScript 与 xterm.js 界面。

开发验收可运行 `Weirdo.exe --devtools --devtools-port 9335 --data-dir <隔离目录>`。正常启动关闭调试端口。原始版权和第三方许可证见 `LICENSE`、`NOTICE.md` 与 `licenses/`。
