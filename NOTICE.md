# Kooky for Windows — source and license notices

This is an independent Windows adaptation of [iAmCorey/kooky](https://github.com/iAmCorey/kooky), source revision `fc54bd9002aa97fd6d67edb9d13c4b5143360fee`. It is not an official Windows release by the original author.

Copyright (c) 2026 kooky contributors. Original MIT license is retained in `LICENSE`; the complete upstream notices are retained in `licenses/KOOKY-NOTICE.md`.

Windows modifications: Swift/AppKit was replaced with a .NET Windows host, Windows ConPTY process management, a persistent local broker, WebView2 and a TypeScript/xterm.js interface. The recursive split-pane model is translated from `Sources/KookyKit/Sessions/PaneNode.swift`; code comments identify the port. Visual spacing, terminal-first workspace organization and dark/light palette follow the source. Original fonts, agent icons and selected terminal palettes are reused with their notices.

Bundled components retain their own licenses: xterm.js and its addons (MIT), Microsoft WebView2 SDK (Microsoft license), .NET runtime (MIT plus bundled notices), Onest and JetBrains Mono (SIL OFL 1.1), lobe-icons (MIT), and Shiki terminal palettes (MIT). Full relevant license texts ship under `licenses/` and with the self-contained .NET runtime.

The Git numstat parser and Gemini session metadata reader are adapted from Kooky's `GitStatusFetcher.swift` and `AgentSessionStores.swift`, with Windows path handling and bounded reads. Native ConPTY interop follows the published Windows API contract and is implemented for this project. No upstream Kooky Swift runtime, Agent harness or model API proxy is bundled.
