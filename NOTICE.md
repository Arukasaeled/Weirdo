# Weirdo — source and license notices

This is an independent Windows adaptation of [iAmCorey/kooky](https://github.com/iAmCorey/kooky), source revision `fc54bd9002aa97fd6d67edb9d13c4b5143360fee`. It is not an official Windows release by the original author.

Copyright (c) 2026 kooky contributors. Original MIT license is retained in `LICENSE`; the complete upstream notices are retained in `licenses/UPSTREAM-NOTICE.md`.

Windows modifications include a Tauri 2/Rust desktop host, Windows ConPTY process management in a persistent native-compiled C# broker, system WebView2 and a TypeScript/xterm.js interface. The recursive split-pane model is translated from `Sources/KookyKit/Sessions/PaneNode.swift`. Visual spacing, terminal-first workspace organization and dark/light palette follow the source. Original fonts, agent icons and selected terminal palettes are reused with their notices. Version 0.2 introduces the Weirdo identity, integrated window controls, direct workspace selection and a persistent file tree.

The Weirdo logo was supplied for this project. The original transparent PNG is preserved in `assets/branding/weirdo-logo-source.png`; application, tray and installer icons are scaled derivatives of that artwork.

Bundled components retain their own licenses, including Tauri (MIT or Apache-2.0), xterm.js and its addons (MIT), Microsoft WebView2 SDK (Microsoft license), .NET NativeAOT components (MIT and component notices), Onest and JetBrains Mono (SIL OFL 1.1), lobe-icons (MIT), and Shiki terminal palettes (MIT). Full relevant license texts ship under `licenses/`.

The Git numstat parser and Gemini session metadata reader are adapted from Kooky's `GitStatusFetcher.swift` and `AgentSessionStores.swift`, with Windows path handling and bounded reads. Native ConPTY interop follows the published Windows API contract and is implemented for this project. No upstream Kooky Swift runtime, Agent harness or model API proxy is bundled.
