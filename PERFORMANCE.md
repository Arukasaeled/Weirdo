# Weirdo 0.2.1 measurements

Measured on this Windows machine on 2026-11-02. CPU percentages use total machine capacity across18 logical processors. Measurements include the native host, system WebView2, ConPTY and actual official CLI processes.

## Startup and package

The installed normal application reached its frontend-ready signal with a visible responsive native window in1888.81,1595.33 and1883.47ms. Median1883.47ms. Each sample used a fresh isolated profile and the normal Windows user environment. UI and broker exited cleanly after each sample. This measures ordinary startup and excludes the one-time import of existing encrypted history.

The Tauri NSIS installer is approximately4.43MiB before the final connection-readiness adjustment. The precise delivered bytes and SHA-256 are in dist/SHA256SUMS.txt. The package uses the system WebView2 runtime and two native executables, without a separately installed .NET runtime.

## Five-terminal resource sample

Twenty one-second intervals were captured with Claude, Codex and three PowerShell terminals. The project still contained625 changed/untracked implementation files, so the broker was actively calculating Git statistics.

| Process group | Average working set MiB | Average private allocation MiB | Average CPU % |
| --- | --- | --- | --- |
| Host and broker |114.60|79.16|1.5050|
| WebView2 |135.72|293.12|0.1169|
| ConPTY consoles |4.73|10.79|0.0000|
| PowerShell |5.82|84.23|0.0000|
| Agent child processes |51.99|420.00|0.0162|

The host itself averaged86.99MiB working set and the broker27.61MiB. The broker accounts for the measured host-group CPU while inspecting the uncommitted source tree. Raw per-process records are in artifacts/acceptance/resources-021.jsonl.

Windows paging strongly affects working sets. Private allocation is included so these measurements cannot be mistaken for fixed process-memory requirements. Agent model/provider behavior, repository size, output volume and the number of visible WebViews change the results.

The new project chooser completed its observed validation/display step in24.3ms before Git and runtime probes completed. Tree expansion, refresh and tab/workspace switching were checked against real directory contents.
