# FlowLedger contributor instructions

## Scope and location

- Work inside this checkout. The initial user-requested checkout is `E:\FlowLedger`; use the active checkout path rather than hardcoding it into source or documentation.
- Respect existing user work. Keep commits grouped around meaningful milestones; do not create a commit for every small edit.
- The current v0.2 preview supports interface/Wi-Fi, elevated ETW, loopback Clash/Mihomo, SSH client TCP candidates, local SQLite history and period reports/CSV. Chinese/English switching is persisted. Existing QUIC/sustained/recovery and explicit-proxy checks passed; OpenSSH custom-port IPv4/IPv6 local banner/KEX accounting and real SQLite transaction crash/replay passed. Real remote authentication/transfers, other SSH clients/jumps/forwarding, WAN/TUN, real PID reuse, forced native loss, long-run and switching/roaming/sleep acceptance remain pending. Real TUN validation is explicitly deferred by the user. Services are not implemented. Synthetic tests are not hardware acceptance; keep claims bounded.
- ETW recovery may touch only new-format sessions matching this user's FlowLedger owner marker with a demonstrably absent/reused owner PID. Never stop live, unconfirmed, foreign-user or legacy sessions. Proxy access is explicit, literal-loopback-only GET; never read client configs/credentials, change rules or enable TUN. Tokens are supplied locally and stay in memory. Temporary validation cores must not take over the user's running proxy.

## Architecture and accounting

- Domain has no OS, UI, database, or network dependencies. Application defines contracts. Windows infrastructure implements platform adapters. Desktop and Collector are composition roots.
- Keep physical-interface, application, loopback, and proxy measurements separate. Never sum overlapping layers as total usage.
- Missing data is not zero. Handle interface identity, counter epochs, counter decreases, timestamp ordering, and PID reuse explicitly.
- History transactions must save facts and cumulative checkpoints atomically; replay is idempotent. Preserve full UInt64 bytes as decimal TEXT, never SQLite SUM/float approximations. Revoke persisted application/SSH attribution when the source invalidates identities. Reports book observation-end timestamps in selected time zones; no cross-period byte prorating or offline backfill. Keep separate interface streams and all measurement layers.
- SSH uses opt-in TCP metadata, known-client candidates and explicit IP/port rules. Do not read SSH configs/credentials/command lines, inspect packet payloads or infer a final server through proxies/jumps/forwarding. Candidate legs are not guaranteed unique SSH transport bytes. Only save local usage/aliases/settings, never tokens. UI translations must update existing states and column headers.
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
