# 开发指南

## 环境

使用受 .NET 10 支持的 Windows 版本和 x64 SDK。`global.json` 以 10.0.100 为最低 SDK 基线，`latestFeature` 接受后续稳定 .NET 10 特性带；不自动选择预览版。WPF 和 Windows 适配器需要 Windows，纯领域代码独立于平台。

命令行开发只需 SDK；Visual Studio 2026 为可选 IDE。当前不需要 WDK、抓包驱动、服务安装或管理员权限。未来支持范围与权限须通过真实采集验证。

## 常用命令

从仓库根目录执行：

```powershell
dotnet restore FlowLedger.slnx
dotnet build FlowLedger.slnx --configuration Release --no-restore
dotnet test --solution FlowLedger.slnx --configuration Release --no-build
dotnet format FlowLedger.slnx --verify-no-changes --no-restore
```

测试使用固定版本 MSTest.Sdk 和 Microsoft.Testing.Platform，版本位于 `global.json`。这是 .NET 10 的 `--solution` 测试命令模式。需要修复代码格式时运行 `dotnet format FlowLedger.slnx --no-restore`，然后重新构建/测试受影响内容。

```powershell
dotnet run --project src/FlowLedger.Desktop --configuration Release --no-build
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --help
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --diagnose
```

命令行无参数时显示帮助，不启动采集。`--diagnose` 输出系统、运行时、逻辑处理器数量及按接口类型/Up 状态分组的数量；不输出接口名称/标识、IP/MAC、SSID、服务器或进程信息。数量可能包含虚拟和过滤层接口，不能证明已识别物理出口。

退出码：0 表示帮助或诊断成功；1 表示接口清单不可用（输出标记为 Unavailable，空列表不表示没有网卡）；2 表示参数无效。

## 项目约定

采用 `.editorconfig` 的 UTF-8 / LF 与文件作用域命名空间。可空性、确定性构建和编译告警视为错误在 `Directory.Build.props` 中统一配置。NuGet 源通过仓库配置固定为官方源，仓库内不保存凭据。

领域层只实现业务规则；Windows API、SQLite、ETW 和代理通信留在平台适配层。Desktop/Collector 负责实际组成；不要让平台实现成为 Domain 依赖。

`.gitignore` 排除 bin/obj、IDE 文件、数据库、捕获文件、诊断日志和本地密钥。个人配置放入 `.local/` 或 `*.local.json`，公开模板使用不含实际凭据的单独示例。

## 提交与验证

每个可解释的阶段形成一次提交，零散编辑可以合并。提交前检查 diff、构建、测试与格式，确认没有本地报告或使用记录。保留已有许可证；新增依赖需要记录用途与许可证。

GitHub Actions 在 Windows 上执行同一套构建、测试、格式和只读诊断检查。它只能证明基础程序可构建运行，不能证明真实 WLAN、代理、SSH 的计量精度。硬件验收必须按[验收计划](validation.md)另行记录。
