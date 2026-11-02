# Windows 交付验收

目标目录 `D:\Anything\kooky-win`，独立 Windows 项目。参考源码固定为 Kooky `fc54bd9002aa97fd6d67edb9d13c4b5143360fee`，原始仓库只读且无修改；MIT、字体、终端、图标许可完整保留。实现采用 .NET Windows 原生宿主、ConPTY、独立 Broker、WebView2 与 xterm.js。

## 真实场景

| 要求 | 实际验证与证据 |
| --- | --- |
| 多项目 | 两个独立 Git 项目并存；切换后进程不变。实际 Windows 目录选择器打开最终项目，固定项目在 MSI 升级后保留。 |
| 官方 Claude/Codex | 两者均运行完整原生 TUI。Claude Read 读取文件并返回 `KOOKY_CLAUDE_OK`；Codex 使用原生 Shell 读出中文文件并返回 `KOOKY_CODEX_OK`。安装版再次取得模型回复。 |
| 原生权限 | 官方首次目录信任、Claude 权限模式、Codex 权限界面原样显示；终端不识别或代答审批，不代理 API。用户已有权限配置保持有效。 |
| 至少五终端 | 两个 Agent、三个 Shell 同时运行，真实 PID 已记录，连续运行超过 74 分钟。 |
| 后台 Tab | 切换项目、Tab、Pane 不关闭进程；UI 进程被终止并重启后五个 PID 全部保持不变。 |
| 分屏与拖拽 | 水平、垂直嵌套分屏；真实拖动 Tab 到其他 Pane 并保留进程；分隔线支持鼠标及键盘；两个原生窗口共享终端服务和布局。 |
| 关闭窗口 | 实际 WM_CLOSE 隐藏到托盘，五个任务存活；全退出会清理所属子进程树。 |
| 会话恢复 | UI 重连回放现有终端；完整 Broker 重启后 Claude/Codex 用相同原生 UUID 恢复原对话。Codex 通过唯一启动标记与官方元数据精确对应，不按最近时间猜测。 |
| Git/活动 | 切换 Shell cwd 到另一分支，实际显示 `verification-cwd`、1 个文件、+1/-0。仅根据进程、输出、退出码显示状态，未把普通输出标为审批。 |
| 可安装产物 | 真实 MSI 安装成功，应用内升级成功，Windows Installer 退出 0；安装 DLL 哈希与构建产物一致。 |
| 自定义 Agent/环境 | 从设置 UI 创建命令与中文环境变量，从菜单启动，真实输出正确并退出 0；DPAPI 密文持久化。 |
| 输入/剪贴板/文件树 | 中文、emoji、ANSI、终端尺寸调整实测；Ctrl Shift V 粘贴与终端文本复制通过；文件树路径插入不发送 Enter。真实中文 IME 的人工候选框体验仍取决于用户输入法。 |
| 桌面能力 | 托盘、独立 HUD、系统快捷键和 Windows 原生通知事件均验证；常规启动不开放 CDP。 |
| 更新/迁移 | 错误 SHA-256 被拒绝；实际应用内升级保留 Agent 配置及固定项目。普通 JSON 迁移、未来版本拒绝覆盖、损坏文件隔离和 EFS 原子保存均验证。 |
| Gemini | 官方 Gemini 原生彩色 TUI 启动、原生 UUID 和信任页实测；未代替用户完成 Gemini 账户登录或模型调用。 |

## 本机配置处理

原有 Claude 设置缺少逗号，且旧 `auto/...` 模型别名不在本机 Magpie 实际模型列表中。原文件哈希保持不变；Kooky 自己的应用数据下保存独立配置副本，并通过加密环境变量使用原有端点/凭据与已验证的 `deepseek/deepseek-v4-flash`。用户的原始 Claude/Codex 配置未被重写。Codex 原有 hook 的错误原样保留，未阻断已验证的模型与工具调用。

Gemini CLI 在自身启动流程中尝试了原生自动更新，最终显示更新成功；Kooky 未修改其配置。测试时使用的临时设置路径被 Gemini 自己拒绝为不可信系统配置，因此该临时覆盖没有生效，已从应用会话中移除。

## 测试边界

ConPTY 13 项、服务层 62 项、Broker 12 项、前端 5 项为非模拟检查/相关单元检查；具体每项输出在各测试项目及验收目录。C 盘继承 EFS 的读写、备份、恢复真实通过；额外 D 盘独立加密创建因系统拒绝明确跳过。

性能按宿主、WebView2、ConPTY、Shell、Agent 分组实测，见 `PERFORMANCE.md`。当前安装包未商业签名；更新支持可信 HTTPS/本地清单，本项目没有外部公共发布源。完整退出或重启操作系统后，恢复的是官方 Agent 对话和布局，任意 Shell 进程内存不能跨系统重启保存。

原始证据集中在 `artifacts/acceptance`。关键文件包括 `codex-turn.png`、`claude-actual-model-result.png`、`five-terminals.json`、`before-ui-restart.json`、`after-ui-restart.json`、`codex-ui-resume-proof.json`、`native-id-proof.json`、`normal-shell-proof.json`、`native-claude-direct-ready.png`、`normal-codex-restored.png`、`git-cwd-proof.json`、`clipboard-proof.json`、`custom-agent-proof.json`、`notification-proof.json`、`auto-update-proof.json`、`final-install-proof.json`。
