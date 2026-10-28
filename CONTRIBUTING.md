# Contributing to FlowLedger

FlowLedger is at the foundation stage. Please read the [development guide](docs/development.md), [accounting rules](docs/accounting.md), and [roadmap](docs/roadmap.md) before changing collection behavior.

1. Describe the concrete problem and the measurement layer affected.
2. Keep changes focused on a coherent milestone. Small edits can be grouped into one reviewable commit; there is no need to commit each file separately.
3. Add meaningful regression tests for accounting behavior, especially resets, missing observations, identity changes, and duplicate counting.
4. Run the documented build, tests, and formatting checks on Windows.
5. Update feature status and validation documentation when behavior changes. A placeholder or unverified adapter must not be advertised as a supported collector.

Code and identifiers use English. The initial desktop UI is Chinese. Keep the English and Chinese READMEs consistent. Preserve Apache-2.0 licensing and document any new dependency's license and purpose.

Do not commit generated build outputs, runtime databases, packet captures, real SSIDs/server histories, proxy subscriptions, API secrets, or personal machine reports. Use synthetic fixtures and redacted diagnostic metadata.

Pull requests should explain what changed, why, which commands passed, and the remaining collection limitations. Actual network-switch, elevated tracing, or remote-server tests must be described separately from unit tests.
