# FlowLedger · 流量簿

<img src="src/FlowLedger.Desktop/Assets/FlowLedger.png" alt="FlowLedger logo" width="112" />

A local network usage ledger for Windows: interface and application traffic, Wi-Fi candidates, Clash/Mihomo observation, SSH client connections, and daily/weekly/monthly reports.

[中文](README.zh-CN.md) · [Usage](docs/usage.md) · [Development](docs/development.md) · [Architecture](docs/architecture.md) · [Roadmap](docs/roadmap.md)

**v0.2 development preview.** Local SQLite history, reports, CSV export, SSH connection statistics and live Chinese/English switching are implemented. OpenSSH IPv4/IPv6 custom-port loopback accounting and SQLite crash/replay checks have passed. Broader SSH, WAN/TUN, network-switch and long-run acceptance remain pending. Real TUN validation is deferred; there is no background service or installer.

## Capabilities and limits

| Capability | Current scope |
|---|---|
| Interface rates and usage | Native GetIfTable2, one-second polling, each interface kept separate |
| Hardware/tunnel/loopback/filter classification | Hardware candidates do not prove an Internet exit |
| Wi-Fi candidate allocation | Opt-in notifications and current-connection reads; known/boundary/unknown partitions conserve observed bytes |
| Route evidence | Read-only summaries; no per-application exit inference |
| Application TCP/UDP v4/v6 | Opt-in administrator ETW; process lifetimes, unknown attribution and loss handling |
| ETW reliability | Controlled QUIC, three 30-second sustained TCP rounds, owned-session fault/crash recovery validated |
| Clash/Mihomo ledger | Separate core and partial connection API deltas, conservative TCP owner association; local explicit-proxy validated |
| SSH client connection ledger | Metadata candidates, process/connection instances, server aliases and exact IP + port rules; OpenSSH v4/v6 custom-port local validation |
| SQLite history | Transactional facts/checkpoints, idempotent replay, unsigned integer precision, quality and attribution revocation |
| Reports | Day/week/month/custom range, selectable time zone, partial-data indicators, CSV export |
| Desktop | Seven pages, simplified explanations, instant Simplified Chinese / English switch saved locally |
| Still planned | Background service, installer, quotas, name-hiding export, retention/backup tools, broad hardware and 72-hour acceptance |

Interface, application, proxy and SSH views overlap. **Never add these layers into a machine-wide total.** SSH is an application subset; forwarding can add candidate TCP legs. Interface traffic includes LAN and loopback/virtual interfaces remain distinct. Missing data is not zero. See [accounting rules](docs/accounting.md) and the [support matrix](docs/support-matrix.md).

## Build and run

Use Windows x64 and a stable **.NET 10 SDK**. Visual Studio is optional. The published desktop needs the .NET 10 Windows Desktop Runtime.

```powershell
git clone https://github.com/wxk38bdc/FlowLedger.git
cd FlowLedger
dotnet restore FlowLedger.slnx
dotnet build FlowLedger.slnx --configuration Release --no-restore
dotnet test --solution FlowLedger.slnx --configuration Release --no-build
dotnet run --project src/FlowLedger.Desktop --configuration Release --no-build
```

Interface collection starts with the window. Enable Wi-Fi allocation and proxy observation separately. **Application and SSH capture require reopening the program as administrator**, then explicitly starting capture. The product never elevates itself, installs a driver/service or changes network/proxy settings. Power boundaries stop application/proxy/SSH capture and require manual restart.

For SSH, start **SSH connections** capture before opening an OpenSSH connection in Windows Terminal. New outbound connections and port 22 can appear as client candidates. For existing custom-port connections or aliases, add the actual server IP, port and alias under **Server aliases & rules** while stopped, then start again. Hostnames are not resolved. Names indicate candidates and rules indicate user confirmation; neither is protocol inspection. Jumps/proxies show locally visible endpoints only. Multiplexed channels and remote-only downloads cannot be assigned to final servers. Other recognized client names include scp/sftp, Xshell and PuTTY/Plink; this milestone's real-client validation prioritizes OpenSSH.

For Clash/Mihomo, enter the HTTP/mixed entry port, use **Find local controller**, enter the token locally, and start observation. Controller and entry ports differ. Tokens remain in memory. Only loopback `/version` and `/connections` GET requests are made; no client configs or rules are read/changed. Active lists can miss short connections and closing tails; core and connection totals are separate. Local validation transfers a synthetic 1 MiB loopback response and does not test TUN/WAN.

## Local history and reports

The desktop normally batches observed data every five seconds, flushing on stop, query and exit, to:

```text
%LOCALAPPDATA%\FlowLedger\history.sqlite3
```

Saved history survives stop, restart and new captures. **Reports** selects one independent ledger, grouping period, inclusive date range and time zone. Weeks start Monday. Intervals are booked at observation end, without proportional splitting across period boundaries. Empty periods mean no observations. CSV exports the selected report, including integer bytes, scope, time zone and quality metadata.

Capture stops when the window closes; unobserved and uncommitted traffic cannot be recovered. Storage failures are shown and retried, with an explicit warning if closing would discard pending data. The unencrypted database may contain local interface/network names, process names and SSH server addresses/aliases. It stays local and contains no passwords, proxy tokens, SSH keys, command lines or packet payloads. CSV currently contains real names. Keep runtime databases and exports private. Retention/backup management and encrypted storage are not implemented. See [usage](docs/usage.md) and [the storage/SSH decision](docs/decisions/0005-local-history-ssh-reports.md).

## Validation

```powershell
dotnet format FlowLedger.slnx --verify-no-changes --no-restore
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --help
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --diagnose
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-history
# Explicit administrator validation, local synthetic traffic only:
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-ssh
```

Ordinary CLI observations are memory-only and anonymous. History validation uses a disposable synthetic database and terminates only its own transaction helper. SSH validation uses the installed OpenSSH client against synthetic loopback banner/KEX endpoints, with user configuration, identity/agent and interactive authentication disabled. It does not test authenticated remote transfers. See [current validation](docs/validation/2026-10-29-ssh-history.md), [ETW evidence](docs/validation/2026-10-29-etw-loopback.md), [reliability/proxy evidence](docs/validation/2026-10-29-reliability-proxy.md), and [remaining acceptance](docs/validation.md).

## Repository

Domain contains platform-free accounting. Application defines monitoring/history contracts and report rules. Windows infrastructure provides native counters/WLAN/ETW/proxy adapters and SQLite. Desktop and Collector compose those pieces. Tests cover accounting, lifecycles, storage, reports and Windows UI/interop. See [architecture](docs/architecture.md) and [contribution instructions](CONTRIBUTING.md).

## License

[Apache License 2.0](LICENSE). TraceEvent, Microsoft.Data.Sqlite, SQLitePCLRaw and SQLite retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md). No Mihomo runtime or test database is distributed.
