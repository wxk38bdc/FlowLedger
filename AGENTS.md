# FlowLedger contributor instructions

## Scope and location

- Work inside this checkout. The initial user-requested checkout is `E:\FlowLedger`; use the active checkout path rather than hardcoding it into source or documentation.
- Respect existing user work. Keep commits grouped around meaningful milestones; do not create a commit for every small edit.
- The current validation build supports live interface sampling and opt-in in-memory Wi-Fi candidate accounting, with boundary and unknown buckets. Application attribution, persistent history, and services are not implemented. Real switching/roaming/sleep validation remains separate from synthetic tests. Keep README status and UI claims honest.

## Architecture and accounting

- Domain has no OS, UI, database, or network dependencies. Application defines contracts. Windows infrastructure implements platform adapters. Desktop and Collector are composition roots.
- Keep physical-interface, application, loopback, and proxy measurements separate. Never sum overlapping layers as total usage.
- Missing data is not zero. Handle interface identity, counter epochs, counter decreases, timestamp ordering, and PID reuse explicitly.
- Add no traffic payload collection, driver/service installation, network changes, or credential discovery as part of an ordinary build or diagnostic command.
- Do not publish machine-specific reports, SSIDs, server addresses, captures, subscriptions, credentials, or runtime databases.

## Validation commands

Run from the repository root on Windows with a stable .NET 10 SDK:

```powershell
dotnet restore FlowLedger.slnx
dotnet build FlowLedger.slnx --configuration Release --no-restore
dotnet test --solution FlowLedger.slnx --configuration Release --no-build
dotnet format FlowLedger.slnx --verify-no-changes --no-restore
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --diagnose
```

- Document real hardware validation separately; a successful build/unit test does not validate live traffic accuracy.
- The CI workflow uses pinned GitHub Action revisions. Verify upstream references when updating them.
- Use UTF-8 and LF. Follow `.editorconfig`. Keep README.md and README.zh-CN.md aligned.
