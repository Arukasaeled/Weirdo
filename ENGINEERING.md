# Weirdo engineering checkpoint

Active source is D:\Anything\Weirdo, an independent Git clone of the previously delivered Windows workbench. The earlier installation has three user terminals running. Keep those processes running while building and verifying the new version with isolated data.

Version 0.2.0 replaces the WinForms desktop with Tauri 2/Rust and system WebView2. The proven ConPTY implementation remains in an independently running NativeAOT broker. Integrate the titlebar with dark/light themes, migrate the product identity, and expose editable Claude startup models while preserving native model selection, endpoint, credentials, permissions, and original CLI files.

The coordinator owns src-tauri, UpdateService.cs, packaging, installation, legal notices, and acceptance. The frontend agent owns web. The backend agent owns Weirdo.Broker, ConPTY and Broker tests. The services agent owns Core services/models, encryption migration, and service tests.

IPC remains JSON lines over a current-user-only named pipe. Tauri exposes host_request and host-event, retaining replay deduplication and ordered input. Each native window gets its own subscriber for cross-window state updates.

AgentProfile.model is optional and accepts arbitrary IDs. Empty values inherit environment and native settings. Current-session model choice remains with the official CLI. Legacy encrypted records require old entropy as a migration fallback. New state and executable names use Weirdo.

Retain verbatim upstream copyrights in legal notices. Original names also remain in compatibility readers and migration fixtures. Product UI, namespaces, runtime metadata, packages and newly written state use Weirdo.

Pending acceptance

- Real Tauri desktop, dark/light integrated titlebar, controls and multiple windows.
- Five real ConPTY terminals and official Claude/Codex TUI.
- Editable model saved and applied, native /model switching through the existing gateway.
- Background persistence, tray/HUD/hotkeys and native session restoration.
- Actual installation, update verification and current-version measurements.
- Full keyword audit with only source attribution and backward compatibility exceptions.

Historical release checks do not verify this migration. Never log credentials, overwrite original CLI files, stop user tasks without authorization, or modify neighboring projects.

Latest steering on 2026-11-02 closes the model question and requests smoother workspace/file selection plus the supplied Weirdo logo. Active version is now 0.2.1. The transparent source logo is preserved under assets/branding and native icons are generated with Tauri's icon converter. Default release output is now the Tauri NSIS EXE, with a current-user Programs directory hook. Model work is frozen.

The original installed version has three live terminals. The first trial UI PID79524 and broker78996 have five terminals and have been used interactively, so preserve that profile under artifacts/acceptance/profile. The coordinator-owned final QA profile is artifacts/acceptance/profile-final with broker79364 and five terminals; UI20880 was intentionally closed and all five PIDs survived. Rediscover PIDs before any mutation.

0.2.0 NSIS installed successfully at LocalAppData/Programs/Weirdo. The installed host differs from the portable binary by exactly three bytes, Tauri's documented bundle marker UNK -> NSS. Broker hash matched. The startup script's strict four-part version check was corrected to accept Tauri's three-part FileVersion. These are pending revalidation against the upcoming0.2.1 logo/workspace build.

The services agent completed workspace.resolve with113 passing checks. The frontend agent owns the new chooser and persistent tree. Root must add dispatch (done), parent the native folder dialog and pass its starting directory (done), finish final packaging, actual workspace/file selection acceptance, startup/resource measurements, and accurate handoff. Original upstream copyright and legacy encryption/identity readers are intentional brand-search exceptions.
