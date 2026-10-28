# FlowLedger · 流量簿

<img src="src/FlowLedger.Desktop/Assets/FlowLedger.png" alt="FlowLedger logo" width="112" />

A local network usage ledger for Windows, designed for per-app and per-Wi-Fi tracking, proxy-aware accounting, SSH traffic statistics, and daily, weekly, and monthly reports.

[中文说明](README.zh-CN.md) · [Development](docs/development.md) · [Architecture](docs/architecture.md) · [Roadmap](docs/roadmap.md)

**Status: Wi-Fi accounting validation / pre-alpha.** Live per-interface rates and opt-in in-memory Wi-Fi candidate accounting are available. Application attribution, persistent history, and historical reports are not implemented yet. This is not a production monitoring tool.

## Why FlowLedger?

Answer questions such as “How much did my laptop use on my phone's hotspot?”, “Which application used that traffic?”, and “How much local data did this SSH connection transfer?”

Physical interface traffic, application traffic, and proxy traffic are related views of the same activity. FlowLedger is designed to keep those measurement layers explicit instead of adding them together. See the [accounting rules](docs/accounting.md).

## Current capabilities

| Capability | Status |
|---|---|
| Windows desktop shell with Chinese navigation and empty states | Available |
| Live interface rates and session usage, with independent interface selection | Available; GetIfTable2 counters, 1-second polling |
| Native hardware/filter/tunnel/loopback classification | Available; hardware candidates are not proof of an Internet exit |
| On-demand current Wi-Fi name query | Available with permission/error fallback; not historical network attribution |
| Opt-in Wi-Fi notifications and in-memory candidate usage | Available; boundary-unallocated and unidentified bytes stay separate |
| Read-only per-interface route evidence | Available; does not infer per-application Internet exits |
| Read-only OS/runtime and grouped network-interface diagnostics | Available |
| Startup baselines, resets, gaps, clock changes, and missing measurements | Connected to live interface sessions and covered by automated tests |
| Per-application traffic collection | Planned; ETW validation first |
| Persistent Wi-Fi history and certified switching/roaming behavior | Planned; live switching/roaming/sleep acceptance remains pending |
| Legacy Clash / Mihomo integration and proxy deduplication | Planned; core/version-specific compatibility |
| SSH client and server usage breakdown | Planned; Xshell and OpenSSH are initial validation targets |
| Local database, daily/weekly/monthly reports, export and quota reminders | Planned |
| Background Windows service and installer | Planned |

The desktop displays local interface aliases. Wi-Fi information is read after an explicit query or while opt-in Wi-Fi accounting is enabled. CLI commands print no interface names, stable IDs, IP/MAC addresses, SSIDs, profile names, destinations, payloads, or secrets. Interface counts include virtual and filter entries. No database or background service is created. Closing the window stops sampling, releases notification subscriptions, and discards session totals.

The overview shows one interface at a time and never sums overlapping physical, tunnel, proxy, or filter layers. A first sample establishes a baseline without counting traffic from before startup. Failed reads, offline/removed adapters, observed identity changes, counter decreases, power notifications, clock jumps, and polling gaps over five seconds break continuity. Rates are unavailable during gaps; valid session totals remain partial. See [validation and remaining limits](docs/validation.md).

On the **网络账本** page, enable Wi-Fi accounting to observe connection/roaming notifications and bracket interface counter reads with connection queries. Candidates use raw SSID bytes, profile, security, and Windows network context; BSSID changes alone do not create a new candidate. These fields cannot prove globally unique network identity. Stable observed intervals are assigned to a candidate; transition intervals remain boundary-unallocated, and missing identity or incomplete notification registration produces unidentified usage. All three partitions sum exactly to the valid bytes observed while accounting is enabled. Pause/resume establishes a fresh attribution baseline. Physical counter continuity uses adapter identity rather than network identity, so valid switching bytes can remain visible in the boundary bucket.

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
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --sample 5
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-counters 5
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --route-summary
```

The current programs run without administrator privileges. Future collection privileges will be validated separately. See [development instructions](docs/development.md) for formatting checks, diagnostic exit codes, and limitations.

`--verify-counters` brackets native endpoint samples with .NET counter reads at the same OS layer; it does not validate application attribution or ISP billing. No comparable hardware interface is an inconclusive result. The optional `--probe-wifi` command prints capability status only and may trigger a Windows location prompt. No permission is granted automatically.

Optional `--watch-wifi 5` enables notifications and current-connection reads for five seconds, then prints anonymous partitions and a byte-conservation result. It may trigger a Windows location prompt and never grants consent automatically. Full ACM/MSM registration indicates subscribed sources, not guaranteed delivery of every notification. See [the design decision and remaining ambiguity](docs/decisions/0002-wifi-candidate-accounting.md).

## Repository layout

```text
src/
  FlowLedger.Domain/                  Accounting rules; no platform dependencies
  FlowLedger.Application/             Contracts, diagnostic models, interface session coordinator
  FlowLedger.Infrastructure.Windows/  Native counters, opt-in WLAN notifications and route evidence
  FlowLedger.Collector/               Sampling, Wi-Fi partitions, reference and route diagnostics
  FlowLedger.Desktop/                 WPF interface monitor and opt-in Wi-Fi ledger
tests/
  FlowLedger.Domain.Tests/            Counter and session accounting regression tests
  FlowLedger.Application.Tests/       Per-interface isolation and lifecycle tests
  FlowLedger.Windows.Tests/           ABI, classification, Wi-Fi fallback and native read tests
docs/                                Product, architecture, validation and decisions
```

## Contributing

Start with [CONTRIBUTING.md](CONTRIBUTING.md) and the [validation plan](docs/validation.md). Contributions should keep planned features clearly distinguished from verified capabilities. Never include real packet captures, private network histories, proxy subscriptions, or credentials in changes or issue reports.

## License

[Apache License 2.0](LICENSE).
