# FlowLedger · 流量簿

A local network usage ledger for Windows, designed for per-app and per-Wi-Fi tracking, proxy-aware accounting, SSH traffic statistics, and daily, weekly, and monthly reports.

[中文说明](README.zh-CN.md) · [Development](docs/development.md) · [Architecture](docs/architecture.md) · [Roadmap](docs/roadmap.md)

**Status: foundation / pre-alpha.** The desktop shell and read-only environment diagnostics are available. Live traffic collection and historical reports are not implemented yet. This is not a production monitoring tool.

## Why FlowLedger?

Answer questions such as “How much did my laptop use on my phone's hotspot?”, “Which application used that traffic?”, and “How much local data did this SSH connection transfer?”

Physical interface traffic, application traffic, and proxy traffic are related views of the same activity. FlowLedger is designed to keep those measurement layers explicit instead of adding them together. See the [accounting rules](docs/accounting.md).

## Current capabilities

| Capability | Status |
|---|---|
| Windows desktop shell with Chinese navigation and empty states | Available |
| Read-only OS/runtime and grouped network-interface diagnostics | Available |
| Counter-delta rules for startup baselines, resets, interface changes, and missing measurements | Implemented with unit tests; not connected to live counters |
| Per-application traffic collection | Planned; ETW validation first |
| Separate usage history for different Wi-Fi networks | Planned; SSID permissions and switching validation first |
| Legacy Clash / Mihomo integration and proxy deduplication | Planned; core/version-specific compatibility |
| SSH client and server usage breakdown | Planned; Xshell and OpenSSH are initial validation targets |
| Local database, daily/weekly/monthly reports, export and quota reminders | Planned |
| Background Windows service and installer | Planned |

Diagnostics contain no interface names, IP/MAC addresses, SSIDs, connection destinations, packet contents, or API secrets. Interface counts include virtual and filter entries; they are not physical adapter counts. No database or background service is created by this build.

## Build and run

Use a supported Windows x64 environment and a stable **.NET 10 SDK**. `global.json` accepts stable .NET 10 feature bands. Visual Studio is optional; Visual Studio 2026 can open `FlowLedger.slnx`.

```powershell
git clone https://github.com/wxk38bdc/FlowLedger.git
cd FlowLedger
dotnet restore FlowLedger.slnx
dotnet build FlowLedger.slnx --configuration Release --no-restore
dotnet test --solution FlowLedger.slnx --configuration Release --no-build
dotnet run --project src/FlowLedger.Desktop --configuration Release --no-build
```

For read-only command-line diagnostics:

```powershell
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --diagnose
```

The current programs run without administrator privileges. Future collection privileges will be validated separately. See [development instructions](docs/development.md) for formatting checks, diagnostic exit codes, and limitations.

## Repository layout

```text
src/
  FlowLedger.Domain/                  Accounting rules; no platform dependencies
  FlowLedger.Application/             Application contracts and diagnostic models
  FlowLedger.Infrastructure.Windows/  Read-only Windows environment adapter
  FlowLedger.Collector/               Diagnostic CLI; future collector entry point
  FlowLedger.Desktop/                 WPF desktop shell
tests/
  FlowLedger.Domain.Tests/            Counter-accounting regression tests
docs/                                Product, architecture, validation and decisions
```

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md) and the [validation plan](docs/validation.md). Contributions should keep planned features clearly distinguished from verified capabilities. Never include real packet captures, private network histories, proxy subscriptions, or credentials in changes or issue reports.

## License

[Apache License 2.0](LICENSE).
