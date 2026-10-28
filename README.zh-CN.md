# FlowLedger · 流量簿

<img src="src/FlowLedger.Desktop/Assets/FlowLedger.png" alt="FlowLedger 标志" width="112" />

面向 Windows 的本地流量账本，计划支持按应用、不同 Wi-Fi、代理路径和 SSH 连接统计流量，并按日、周、月及自定义账期汇总。

[English](README.md) · [开发指南](docs/development.md) · [架构设计](docs/architecture.md) · [任务路线](docs/roadmap.md)

**当前处于 Wi-Fi 分账验证阶段。** 已能显示真实接口速率和本次用量，并按需启用 Wi-Fi 网络候选的内存分账；尚未实现应用归属、历史持久化和报表。

## 已实现与计划功能

| 功能 | 当前状态 |
|---|---|
| 中文桌面外壳：总览、网络账本、应用统计、远程连接、运行环境 | 已实现导航和空状态，没有虚构的流量数据 |
| 所选接口的实时速率与本次运行用量 | 已实现；GetIfTable2 读取，每秒采样，关闭窗口停止且不保存历史 |
| 硬件、隧道、回环、虚拟/其他及过滤层分类 | 已实现；依据原生类型/标志，物理候选不等于已确认互联网出口 |
| 手动查询当前 Wi-Fi 名称 | 已实现；处理权限拒绝、未连接及不可用，不据此进行历史分账 |
| Wi-Fi 通知与本次运行分账 | 已实现按需启停；网络候选、边界未分配、未识别分别展示 |
| 只读出口关系检查 | 已实现路由条目摘要；不推断每个应用的互联网出口 |
| 只读系统、运行时和网络接口分类诊断 | 已实现；不读取接口名称、地址、SSID 或连接目的地 |
| 首次基线、重置、缺口、时钟变化和缺失测量规则 | 已接入实时接口会话，有自动化测试覆盖 |
| 每个应用的流量 | 待 ETW 采集能力验证 |
| Wi-Fi 持久历史及切换/漫游实机验收 | 待完成；当前分账只在内存中 |
| Clash / Mihomo 去重与原应用归属 | 待版本兼容性和三条路径验证 |
| Xshell / OpenSSH 的本地流量、服务器专题 | 待连接归属验证 |
| SQLite 历史、日周月报表、CSV、额度提醒 | 待开发 |
| 后台采集服务、安装包 | 待开发 |

## 统计原则

- 网络出口、应用和代理分别展示统计层级，不能相加形成虚高的总用量。
- Wi-Fi 网卡不等于 Wi-Fi 网络；同一网卡连接家庭网络、公司网络和热点时要分别记账。
- 未记录、无法归属和实际零消耗分别表示，不把缺失数据写成零。
- SSH 统计的是本地实际收发；服务器自身下载至远端磁盘的文件不会全部经过本地。
- 物理接口计数也包含局域网等流量，不承诺等于运营商账单或无线空口全部开销。

详见[记账规则](docs/accounting.md)。

总览一次只展示一张网卡，不将物理、隧道和过滤层用量叠加。首次采样只建立基线；断线、接口消失、读取失败、已观测的连接身份变化、计数回退、休眠/恢复通知、时钟跳变或超过五秒的采样间隔会中断计量并重新建立基线。缺口时速度显示不可用，已累计用量保留为部分记录，不补算断档期间字节。

在**网络账本**页点击“启用 Wi-Fi 分账”，程序订阅连接/漫游通知，并在接口计数前后查询连接信息。候选由原始 SSID、配置、安全类型和 Windows 网络上下文组成；BSSID 变化不会单独创建新候选，但这些字段仍不能证明网络在全球唯一。稳定观测区间归属候选，切换区间记为边界未分配，身份或通知不足时记为未识别，三者之和逐字节等于启用期间纳入分账的有效字节。暂停后保留已有记录，恢复时重新建立归属基线；关闭窗口停止采集并释放通知订阅，记录丢失。网卡计数连续性与网络身份分开，确保可测的切换字节能够进入边界桶。

## 构建与运行

首批目标为受支持的 Windows x64 环境，安装稳定版 **.NET 10 SDK**。可使用命令行，或用 Visual Studio 2026 打开解决方案。

```powershell
git clone https://github.com/wxk38bdc/FlowLedger.git
cd FlowLedger
dotnet restore FlowLedger.slnx
dotnet build FlowLedger.slnx --configuration Release --no-restore
dotnet test --solution FlowLedger.slnx --configuration Release --no-build
dotnet run --project src/FlowLedger.Desktop --configuration Release --no-build
```

运行命令行环境诊断：

```powershell
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --diagnose
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --sample 5
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-counters 5
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --route-summary
```

当前程序无需管理员权限，不安装服务、不创建数据库，也不修改网络设置。接口诊断的条目数量包含虚拟和过滤层接口，不能当作实际网卡数。未来采集所需权限将单独验证。

桌面显示本机接口别名，Wi-Fi 信息在手动查询或启用分账期间读取。上述命令行输出不含接口名称、稳定标识、IP/MAC、SSID、配置名称、服务器地址、报文内容或密钥。`--verify-counters` 用 .NET 同层计数在原生样本两端前后夹取参考范围；它不证明应用归属或运营商账单精度，没有可比较硬件接口时视为结论不足。

可选的 `--probe-wifi` 仅输出能力状态；`--watch-wifi 5` 订阅通知并持续查询五秒，输出匿名分区与字节守恒结果。两者可能触发 Windows 位置权限提示，程序不自动授予权限。完整注册 ACM/MSM 表示已订阅对应通知源，不保证每条通知都无遗漏。[候选设计决策](docs/decisions/0002-wifi-candidate-accounting.md)和[验收计划](docs/validation.md)记录了限制。

## 开发文档

- [产品范围](docs/product.md)：第一版要解决的问题与功能边界。
- [架构](docs/architecture.md)：项目依赖与采集、界面、存储的划分。
- [记账规则](docs/accounting.md)：去重、基线、未知归属及时间规则。
- [开发指南](docs/development.md)：构建、测试、格式检查及贡献方式。
- [任务路线](docs/roadmap.md)：阶段进度、任务编号与放行条件。
- [验收计划](docs/validation.md)：直连、代理、TUN、WLAN、SSH 等场景。
- [架构决策](docs/decisions/0001-foundation.md)：技术选择及重新评估条件。

项目沿用 [Apache License 2.0](LICENSE)。
