# Third-party notices

FlowLedger remains licensed under [Apache-2.0](LICENSE). Dependencies retain their own licenses. No third-party source code is vendored in the repository.

## Microsoft.Diagnostics.Tracing.TraceEvent 4.0.0

Purpose: decode Windows kernel process and TCP/UDP network metadata and manage explicitly enabled real-time ETW sessions. The Windows infrastructure project owns the direct package reference; the Collector also uses its transitive API for independent typed validation and owned-session recovery tests. Package author: Microsoft. Project: [microsoft/perfview, v4.0.0](https://github.com/microsoft/perfview/tree/v4.0.0). Package license: MIT.

Upstream [LICENSE.TXT](https://github.com/microsoft/perfview/blob/v4.0.0/LICENSE.TXT):

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The .NET 10 Windows dependency graph also resolves Microsoft.Diagnostics.NETCore.Client 0.2.661903, Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2, Microsoft.Extensions.Logging.Abstractions 8.0.3 and System.Reflection.TypeExtensions 4.7.0. These Microsoft packages use MIT licenses; their upstream copyright notices remain applicable. The SDK may satisfy additional package references with framework assemblies. Inspect the actual graph with `dotnet list src/FlowLedger.Infrastructure.Windows package --include-transitive` after changing SDK or dependencies; do not treat this list as a frozen lock file.

The .NET runtime and Microsoft test SDK/packages are separately distributed development/runtime prerequisites. Their notices are included in their original distributions.
