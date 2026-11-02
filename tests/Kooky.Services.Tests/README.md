# Services verification

Run `dotnet run --project tests/Kooky.Services.Tests/Kooky.Services.Tests.csproj` from the project root on Windows.

Uses real DPAPI encryption, actual subprocess execution, temporary Git repositories, Windows junctions, and local metadata fixtures. No Agent config is changed and no model API is invoked. Each run owns a unique project-local fixture directory and cleans it after completion.

The suite covers durable storage, corruption quarantine and last-good recovery, future-schema rejection, v0 migration, concurrent writers, encrypted custom environment variables, environment isolation, command paths containing spaces/Unicode/ampersands, exact native resume UUIDs, Codex launch-marker attribution after delayed onboarding and same-directory concurrent launches, Git staged/unstaged/untracked/binary/unborn/rename accounting, and tree traversal through junctions.

Services integration notes:

- `StateStore.RecoveryWarnings` exposes any recovery performed; a future schema is rejected without mutation.
- `GitInfo.Error` can accompany valid repository data when line totals are partial due to a large/unreadable file or a timeout. Display this instead of claiming complete counts.
- Codex discovery binds an interactive CLI header only when its originator ends with the exact per-launch `/Kooky:<TrackingId>` marker. The child-only environment retains the existing originator prefix. Startup delays and same-directory parallel sessions do not change attribution. Legacy unmarked sessions and duplicate native IDs for one marker remain unknown; supplied native UUIDs always use exact `codex resume UUID`.
- Native EXE commands launch directly and use Windows argument quoting. Use an explicit `powershell.exe -Command` or `pwsh.exe -Command` for shell expressions. Existing script-launcher handling remains in place.
- Shell cwd integration lives only in the launched PowerShell process. No profile scripts are written.
