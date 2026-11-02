# Weirdo 0.2.1 verification

Verified on Windows on 2026-11-02. The installed application is a Tauri 2/Rust desktop with system WebView2 and a separate NativeAOT ConPTY broker. The supplied transparent logo is used in the interface and Windows icons.

## Automated checks

158 checks passed across frontend8, ConPTY13, services113 and the published native broker24. One additional D-volume encryption fixture is explicitly unavailable because Windows rejects individually encrypted file creation there. Actual C-volume inherited-EFS persistence and model-menu updates passed.

## Actual desktop behavior

- Official Claude and Codex were launched through the desktop UI. Native model/tool output and native conversation UUIDs were preserved. The installed version resumed the saved conversations.
- Five independent terminals were running together. Closing the earlier UI retained all five PIDs. Restoring the UI and cold conversation recovery were checked separately.
- The new project chooser handled a quoted Windows path, rejected a missing directory, listed pinned/open projects, and opened a directory selected through the real Windows folder dialog. The native dialog started in the current project.
- Opening the measured project took24.3ms from Enter to the next frame after directory validation. Git/environment indicators were still loading when the project appeared.
- File-tree DOM, expanded src/组件 directories and selected 你好 world.ts survived tab and workspace switches. Refresh exposed a newly created real file while retaining expansion and selection.
- Enter on the Chinese filename inserted its quoted path into real PowerShell, with the caret remaining on the input line. No Return was submitted.
- Main, secondary and activity windows were exercised. Repeated mouse splits remained effective after a secondary window opened and closed. The passive window retained the main window's newer layout.
- Dark and light integrated titlebars, window controls and the real supplied logo were inspected. Normal launches keep debugging disabled.
- The Tauri NSIS installer completed successfully in the current user's Programs directory. Native broker bytes match the packaged broker. Tauri stamps its installed host with the NSS bundle marker, accounting for the three-byte difference from the portable host's UNK marker.

The default data migration copied597 existing native configuration/history files and imported the existing profiles, workspaces and session records. Original source data remains available. The initial migration copies encrypted files and can take longer than a normal launch. The host now waits for migration readiness and reports an early service-process failure promptly.

Source attribution, original copyrights and narrowly scoped legacy-data readers retain the original upstream name. Current UI branding, application/package names and new code namespaces use Weirdo. Existing historical conversation paths retain their original directory identity.

Private raw evidence is excluded from Git and installers. Key files are artifacts/acceptance/install-021.json, startup-20261102-204912.json, chooser-021.json, file-tree-021.json, workspace-switch-021.json, tree-refresh-021.json, path-insert-021.json, multiwindow-021.json, runtime-021.json, default-migration-021.json and the generated test results. Screenshots include weirdo-021-files.png and synced-native-picker.png.

The package is unsigned. The configured development update feed is local. A public update server has not been provisioned.
