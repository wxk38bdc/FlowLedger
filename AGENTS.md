# FlowLedger contributor instructions

## Scope and location

- Work inside this checkout. The initial user-requested checkout is `E:\FlowLedger`; use the active checkout path rather than hardcoding it into source or documentation.
- Respect existing user work. Keep commits grouped around meaningful milestones; do not create a commit for every small edit.
- The current validation build supports live interface/Wi-Fi sampling, opt-in elevated ETW, and a loopback-only Clash/Mihomo proxy ledger. Controlled QUIC v4/v6, three 30-second sustained TCP rounds and owned-session fault/crash recovery passed; explicit-proxy attribution passed on the user's Clash and isolated Mihomo 1.19.33. These are bounded scenarios: WAN/TUN accuracy, real PID reuse, forced native loss, long-run stability and switching/roaming/sleep remain unverified. Synthetic tests are not hardware acceptance. Persistent traffic history and services are not implemented. Keep README status and UI claims honest.
- ETW recovery may touch only new-format sessions matching this user's FlowLedger owner marker with a demonstrably absent/reused owner PID. Never stop live, unconfirmed, foreign-user or legacy sessions. Proxy access is explicit, literal-loopback-only GET; never read client configs/credentials, change rules or enable TUN. Tokens are supplied locally and stay in memory. Temporary validation cores must not take over the user's running proxy.

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
