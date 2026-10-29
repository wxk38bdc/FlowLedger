# 开发指南

## 环境

使用受 .NET 10 支持的 Windows 版本和 x64 SDK。`global.json` 以 10.0.100 为最低 SDK 基线，`latestFeature` 接受后续稳定 .NET 10 特性带；不自动选择预览版。WPF 和 Windows 适配器需要 Windows，纯领域代码独立于平台。

命令行开发只需 SDK；Visual Studio 2026 为可选 IDE。构建、测试及接口/Wi-Fi 功能无需管理员；明确启用的应用 ETW 原型需要手动提升后的管理员令牌。不需要 WDK、抓包驱动或服务安装。受控回环已实测，其他系统/路径的支持与精度仍待验证。

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
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --route-summary
```

命令行无参数时显示帮助，不启动采集。`--diagnose` 输出系统、运行时、逻辑处理器数量及按接口类型/Up 状态分组的数量；不输出接口名称/标识、IP/MAC、SSID、服务器或进程信息。数量可能包含虚拟和过滤层接口，不能证明已识别物理出口。

`--sample <1..60>` 在内存中逐接口采样指定秒数后输出匿名会话摘要，不输出启动前累计量。摘要包含过滤层和虚拟条目，但不会跨接口求和；Slot 只是本次输出的序号。没有有效测量时退出 1。`--verify-counters <1..60>` 在原生起止样本前后分别读取 .NET GetIPStatistics，检查原生绝对计数及差分是否落在参考上下界内；不在不同采样时刻强求字节完全相等。它是同一 OS 计数层的调用/布局交叉检查，不是独立抓包或 ISP 账单验收。没有可比较的在线硬件接口、原生读取失败或超出参考边界时退出 1。

可选 `--probe-wifi` 仅查询在线硬件 WLAN 的当前连接并输出状态和错误码，不输出名称、BSSID 或 SSID；Windows 可能提示位置权限。不自动操作该提示，不修改权限、不触发网络扫描；无适用接口时退出 1。返回 Available/AccessDenied/Disconnected/Unavailable 是能力结果，不能单凭退出码 0 认定名称可用。

可选 `--watch-wifi <1..60>` 订阅 ACM/MSM 通知，在接口读取前后查询连接信息，输出匿名分区和 PartitionsMatch。退出时 using 释放监听器。权限不足时仍可输出未识别网络字节；没有可观察的硬件 WLAN 或有效区间时退出 1。Coverage=Full 仅表示成功注册两个通知源，不能单凭它证明所有事件完整送达。该命令与桌面“启用 Wi-Fi 分账”均为显式操作；CI 不执行它们，避免触发位置权限提示。网络名、配置名、原始 SSID 和 BSSID 不进入 CLI 输出。

`--route-summary` 返回匿名逐接口路由证据，只包含类型及默认/其他前缀条目数；不是路由选择或应用出口判定。无需断网的验证可重复运行 watch-wifi、route-summary 和 verify-counters，但不能以此代替真正网络切换和睡眠恢复。

退出码：0 表示帮助、诊断/采样成功或探测获得结果；1 表示读取失败、无适用数据或参考校验不通过；2 表示参数无效。`--diagnose` 的接口清单不可用输出为 Unavailable，空列表不表示没有网卡。

## 应用 ETW 实验

开发与普通测试不提升权限。需实机验证时，由用户自行打开管理员终端或以管理员身份打开桌面软件，程序不触发 UAC。然后从仓库根目录运行：

```powershell
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --watch-apps 5
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-apps

dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --validate-apps
```

watch 每秒查询健康计数，1..60 秒后停止自己的唯一命名会话并排空消费者，输出匿名实例/传输类别/未知桶/丢失和守恒；不输出名称/PID。Resources 包含经过秒数、进程 CPU 毫秒、除以逻辑处理器数量后的整机 CPU 百分比，以及定时采样到的工作集/私有内存最大值；不包含内核 ETW 缓冲区，不保证抓到瞬时峰值。无权限、无事件、已知丢失/拒收、身份失效、容量不足或健康计数不可读返回 1；参数错误返回 2。未识别不等于漏收，守恒不等于精度验收。

verify 仅在权限通过后生成有限本机回环 TCP4/TCP6 各 128 KiB、UDP4/UDP6 各 8 KiB；socket 两端属于同一进程，参考收发各相同。返回四类双向可观察、测试进程归属及质量/守恒结果，失败或不支持 IPv6 视为结论不足。它不验证 size 与 socket 字节相等，不访问公网、不改防火墙或网卡。原始 ETW size 语义及独立同层精度须另验收。

validate 在权限通过后运行三轮，每轮 16 个有限子进程。分别覆盖四类传输的不等量收发、24 次短交换和 8 路 TCP 并发（并发部分每轮共 96 MiB 业务收发量）；所有目标地址硬编码为 loopback。另建 `FlowLedger.Reference.<owner>.<PID>.<创建 ticks>.<GUID>` 内存会话，直接用 TraceEvent 类型化 size/PID，不复用生产解码器/账本。每个测试进程须完成送达、退出，并观察到唯一 Start/Stop；排空两条会话后逐实例/协议/方向精确比较 socket 参考、类型化参考和生产账本。任何不符/不支持/质量异常退出 1；全部通过退出 0。输出匿名槽位和合成参考量，不输出 PID/端口或背景流量明细。

内部 `--app-probe` 工作者仅由 validate 使用：严格限制传输、端口、单次字节、交换数（最多 32）和每进程业务收发总量（最多 16 MiB），等待父进程信号，20 秒超时；没有 ETW 或外部地址参数。父进程为每个探测设 25 秒边界，异常时只清理自己创建的准确子进程和监听器。命令自身不提权、不创建 ETL、不改防火墙/网络配置，也不安装服务。参考和生产共享内核源，不能作为独立的事件送达真值；资源数据包含双消费者及父进程回环服务，不含子进程 CPU 或内核缓冲区。并发吞吐是包含启动/关闭的短时业务吞吐，不是物理网卡带宽或持续采集承诺。方法及结论见[实测记录](validation/2026-10-29-etw-loopback.md)。

测试通过合成元数据和假 ETW 会话验证，不要求 CI 拥有管理员令牌；普通权限测试在已提升 runner 上明确跳过，避免测试隐式创建内核会话。CI 不执行应用原生采集命令。该依赖用途/许可见 [第三方声明](../THIRD-PARTY-NOTICES.md)；完整边界见 [ADR 0003](decisions/0003-application-etw-validation.md)。

单元测试包括领域计数/会话规则、应用协调器的接口隔离和生命周期、Windows SDK ABI、类型/标志分类、Wi-Fi 失败和原生读取，以及真实桌面视图模型的异步启停/退出时序（使用合成数据源，无需创建窗口）。Windows.Tests 因引用 Desktop 使用 Windows Desktop Runtime。CI 额外执行一秒采样，但不要求托管 runner 存在真实物理硬件，不执行 Wi-Fi 名称查询或网络设置操作。

## 可靠性与代理验证

新增管理员有界验收：`--verify-reliability <15..300>` 三轮本机 QUIC v4/v6 + 每轮指定秒数的 4 路持续 TCP + 异常恢复；`--verify-etw-recovery` 单独验证自有会话被中断及自有测试辅助进程被终止后的回收。只终止自己创建的测试进程，不影响用户客户端。QUIC 使用合成证书及固定证书校验，Windows 临时私钥容器正常释放清理，不安装证书/信任或修改 TLS。无权限返回 1，不创建探测；不支持 QUIC 不能算通过。

代理命令（无需自动提权；无法确认核心/应用进程时保守降级）：

```powershell
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --watch-proxy 5 http://127.0.0.1:9090 7890
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-proxy http://127.0.0.1:9090 7890
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-tcp-owners
```

示例端口不是自动检测结果。需要认证时，操作者在运行进程的环境中明确提供 `FLOWLEDGER_CONTROLLER_SECRET`；不要放到命令行参数、版本控制或聊天。程序不读取客户端凭据。桌面可用“查找本机控制器”和密钥框。watch 输出匿名分组，核心总量与连接明细独立；无数据/失败/断档/重置/容量不足返回 1。verify-proxy 传输合成 1 MiB loopback HTTP 响应，要求唯一已确认测试原应用及合理 API 增量，失败返回 1；不是 TUN/公网验收。verify-tcp-owners 仅核对真实本机 v4/v6 socket 归属，不输出四元组。非法参数返回 2。

运行辅助文件位于 LocalApplicationData/FlowLedger：`etw-owner.id` 是本用户随机会话标识；桌面 `proxy-validation.json` 只记录合成验证的匿名质量结果，不含密钥或连接明细。CLI 直接输出匿名结果，不写桌面报告。它们不构成流量数据库；关闭仍丢失用量。丢失/损坏 owner 标识时不强行回收旧会话，也不清理其他用户/工具会话。完整边界见 [ADR 0004](decisions/0004-proxy-accounting.md)。

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
