# FlowLedger · 流量簿

<img src="src/FlowLedger.Desktop/Assets/FlowLedger.png" alt="FlowLedger 标志" width="112" />

面向 Windows 的本地流量账本，计划支持按应用、不同 Wi-Fi、代理路径和 SSH 连接统计流量，并按日、周、月及自定义账期汇总。

[English](README.md) · [开发指南](docs/development.md) · [架构设计](docs/architecture.md) · [任务路线](docs/roadmap.md)

**当前处于接口采集验证阶段。** 已能读取真实网卡计数，显示所选接口的实时上传/下载速度和本次运行用量；尚未实现应用归属、Wi-Fi 历史分账和历史报表。

## 已实现与计划功能

| 功能 | 当前状态 |
|---|---|
| 中文桌面外壳：总览、网络账本、应用统计、远程连接、运行环境 | 已实现导航和空状态，没有虚构的流量数据 |
| 所选接口的实时速率与本次运行用量 | 已实现；GetIfTable2 读取，每秒采样，关闭窗口停止且不保存历史 |
| 硬件、隧道、回环、虚拟/其他及过滤层分类 | 已实现；依据原生类型/标志，物理候选不等于已确认互联网出口 |
| 手动查询当前 Wi-Fi 名称 | 已实现；处理权限拒绝、未连接及不可用，不据此进行历史分账 |
| 只读系统、运行时和网络接口分类诊断 | 已实现；不读取接口名称、地址、SSID 或连接目的地 |
| 首次基线、重置、缺口、时钟变化和缺失测量规则 | 已接入实时接口会话，有自动化测试覆盖 |
| 每个应用的流量、不同 Wi-Fi 分账 | 待采集能力验证 |
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
```

当前程序无需管理员权限，不安装服务、不创建数据库，也不修改网络设置。接口诊断的条目数量包含虚拟和过滤层接口，不能当作实际网卡数。未来采集所需权限将单独验证。

桌面显示本机接口别名，Wi-Fi 名称仅在手动查询时读取。上述命令行输出不含接口名称、稳定标识、IP/MAC、SSID、服务器地址、报文内容或密钥。`--verify-counters` 用 .NET 同层计数在原生样本两端前后夹取参考范围；它不证明应用归属或运营商账单精度，没有可比较硬件接口时视为结论不足。可选的 `--probe-wifi` 仅输出能力状态，可能触发 Windows 位置权限提示，程序不自动授予权限。限制与验证记录见[验收计划](docs/validation.md)。

## 开发文档

- [产品范围](docs/product.md)：第一版要解决的问题与功能边界。
- [架构](docs/architecture.md)：项目依赖与采集、界面、存储的划分。
- [记账规则](docs/accounting.md)：去重、基线、未知归属及时间规则。
- [开发指南](docs/development.md)：构建、测试、格式检查及贡献方式。
- [任务路线](docs/roadmap.md)：阶段进度、任务编号与放行条件。
- [验收计划](docs/validation.md)：直连、代理、TUN、WLAN、SSH 等场景。
- [架构决策](docs/decisions/0001-foundation.md)：技术选择及重新评估条件。

项目沿用 [Apache License 2.0](LICENSE)。
