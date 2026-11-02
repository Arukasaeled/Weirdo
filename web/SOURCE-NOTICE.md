# Weirdo frontend source and assets

Weirdo adapts the pane-tree semantics in `src/layout.ts` from
`iAmCorey/kooky`, commit `fc54bd9002aa97fd6d67edb9d13c4b5143360fee`,
`Sources/KookyKit/Sessions/PaneNode.swift` (MIT, copyright 2026 kooky contributors).
Windows changes include typed JSON persistence, immutable tree transformations,
DOM rendering, per-pane tab transfer and Tauri host IPC.

The dense sidebar, per-pane tabs and compact status bars follow Kooky's product
layout. `src/terminal.ts` adapts the bundled GitHub/Shiki ANSI palettes; the dark
terminal background is adjusted to Kooky's screenshot chrome.

`public/fonts/Onest.ttf` and `JetBrainsMono-Regular.ttf` are copied without
modification from Kooky's resources (SIL Open Font License 1.1). Agent PNG marks
for Claude Code, Codex and Gemini are copied from Kooky's lobe-icons assets (MIT).
Original upstream notices are preserved in the repository's `licenses` directory.

The terminal renderer is xterm.js and its Fit/Search/WebGL/Unicode 11 addons (MIT).
No terminal output or Agent activity is simulated.
