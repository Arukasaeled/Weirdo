# Weirdo 0.2.1 measurements

Measured on Windows on 2026-11-02 with18 logical processors. CPU percentages use total machine capacity. The process groups cover the native host, system WebView2, ConPTY, three PowerShell terminals and the official Claude/Codex processes.

## Startup and package

Three installed normal launches reached the frontend-ready signal with visible responsive windows in1888.81,1595.33 and1883.47ms. Median1883.47ms. Each sample used a fresh isolated profile and the normal Windows user environment, with debugging disabled. UI and broker exited cleanly after every sample. One-time import of existing encrypted histories is excluded.

The final Tauri NSIS installer is4,643,718bytes, approximately4.43MiB. SHA-256 is F1DC11D833666F8DA762BC1A62BBEA67FA9DBCA921748F45F1017C15D9406C6A. It uses system WebView2 and two native executables, without a separately installed .NET runtime.

## Five-terminal resource sample

Twenty one-second samples were collected by a separate PowerShell observer. The existing sampler was loaded into the observer process so its CPU cost was excluded from the measured application. The five terminal sessions remained unchanged throughout the sample.

| Process group | Average working set MiB | Average private allocation MiB | Average CPU % |
| --- | --- | --- | --- |
| Host and broker |54.319|30.506|0.076339|
| WebView2 |226.789|361.169|0.120577|
| ConPTY consoles |55.768|10.562|0.000000|
| Three Shells |114.426|80.570|0.005587|
| Agent child processes |152.705|424.754|0.016549|

Windows paging strongly affects working sets, which can include shared pages. Private allocation is included so the numbers cannot be mistaken for fixed physical-memory requirements. Provider behavior, repository size, terminal output and visible WebViews change the results.

Raw evidence is artifacts/acceptance/resources-021-external.jsonl. The observer PID70380 was outside every measured process tree. Earlier JSONL samples remain available, but their host CPU includes the cost of running the sampler inside the broker and must not be used as application idle CPU.

## Navigation

The measured project opening completed directory validation and displayed the workspace in24.3ms from Enter to the next frame. Git and environment probes completed later. Real tests retained expanded directories and selection across tabs/workspaces, and refresh exposed a newly created file.
