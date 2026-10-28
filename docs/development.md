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
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --sample 5
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-counters 5
```

命令行无参数时显示帮助，不启动采集。`--diagnose` 输出系统、运行时、逻辑处理器数量及按接口类型/Up 状态分组的数量；不输出接口名称/标识、IP/MAC、SSID、服务器或进程信息。数量可能包含虚拟和过滤层接口，不能证明已识别物理出口。

`--sample <1..60>` 在内存中逐接口采样指定秒数后输出匿名会话摘要，不输出启动前累计量。摘要包含过滤层和虚拟条目，但不会跨接口求和；Slot 只是本次输出的序号。没有有效测量时退出 1。`--verify-counters <1..60>` 在原生起止样本前后分别读取 .NET GetIPStatistics，检查原生绝对计数及差分是否落在参考上下界内；不在不同采样时刻强求字节完全相等。它是同一 OS 计数层的调用/布局交叉检查，不是独立抓包或 ISP 账单验收。没有可比较的在线硬件接口、原生读取失败或超出参考边界时退出 1。

可选 `--probe-wifi` 仅查询在线硬件 WLAN 的当前连接并输出状态和错误码，不输出名称、BSSID 或 SSID；Windows 可能提示位置权限。不自动操作该提示，不修改权限、不触发网络扫描；无适用接口时退出 1。返回 Available/AccessDenied/Disconnected/Unavailable 是能力结果，不能单凭退出码 0 认定名称可用。

退出码：0 表示帮助、诊断/采样成功或探测获得结果；1 表示读取失败、无适用数据或参考校验不通过；2 表示参数无效。`--diagnose` 的接口清单不可用输出为 Unavailable，空列表不表示没有网卡。

单元测试包括领域计数/会话规则、应用协调器的接口隔离和生命周期，以及 Windows SDK ABI、类型/标志分类、Wi-Fi 失败和原生读取。CI 额外执行一秒采样，但不要求托管 runner 存在真实物理硬件，不执行 Wi-Fi 名称查询或网络设置操作。

## 本地桌面版本

完成 Release 构建后，可将桌面程序发布到固定的本地目录：

```powershell
dotnet publish src/FlowLedger.Desktop --configuration Release --no-build --no-restore --output artifacts/desktop
```

运行 `artifacts/desktop/FlowLedger.Desktop.exe`，或创建指向该文件的 Windows 桌面快捷方式，工作目录设为 `artifacts/desktop`。这是依赖框架的本地发布，需要 .NET 10 Windows Desktop Runtime；SDK 已包含所需运行时。保留发布目录中的全部文件，单独复制 exe 无法运行。更新发布前先关闭该程序窗口。

程序图标与侧栏标志来自 `src/FlowLedger.Desktop/Assets/`，设计来源及生成提示词见[品牌资源](branding.md)。本地发布目录和个人快捷方式不加入版本控制。

## 项目约定

采用 `.editorconfig` 的 UTF-8 / LF 与文件作用域命名空间。可空性、确定性构建和编译告警视为错误在 `Directory.Build.props` 中统一配置。NuGet 源通过仓库配置固定为官方源，仓库内不保存凭据。

领域层只实现业务规则；Windows API、SQLite、ETW 和代理通信留在平台适配层。Desktop/Collector 负责实际组成；不要让平台实现成为 Domain 依赖。

`.gitignore` 排除 bin/obj、IDE 文件、数据库、捕获文件、诊断日志和本地密钥。个人配置放入 `.local/` 或 `*.local.json`，公开模板使用不含实际凭据的单独示例。

## 提交与验证

每个可解释的阶段形成一次提交，零散编辑可以合并。提交前检查 diff、构建、测试与格式，确认没有本地报告或使用记录。保留已有许可证；新增依赖需要记录用途与许可证。

GitHub Actions 在 Windows 上执行同一套构建、测试、格式和只读诊断检查。它只能证明基础程序可构建运行，不能证明真实 WLAN、代理、SSH 的计量精度。硬件验收必须按[验收计划](validation.md)另行记录。
