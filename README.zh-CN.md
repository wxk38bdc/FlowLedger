# FlowLedger · 流量簿

面向 Windows 的本地流量账本，计划支持按应用、不同 Wi-Fi、代理路径和 SSH 连接统计流量，并按日、周、月及自定义账期汇总。

[English](README.md) · [开发指南](docs/development.md) · [架构设计](docs/architecture.md) · [任务路线](docs/roadmap.md)

**当前处于基础架构阶段，尚未实现持续流量采集。** 现在可以构建和运行桌面外壳、检查基础运行环境，以及执行流量计数规则测试。

## 已实现与计划功能

| 功能 | 当前状态 |
|---|---|
| 中文桌面外壳：总览、网络账本、应用统计、远程连接、运行环境 | 已实现导航和空状态，没有虚构的流量数据 |
| 只读系统、运行时和网络接口分类诊断 | 已实现；不读取接口名称、地址、SSID 或连接目的地 |
| 接口累计计数差分、首次基线、重置和缺失测量规则 | 已实现领域规则与单元测试；尚未接入实时计数 |
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
```

当前程序无需管理员权限，不安装服务、不创建数据库，也不修改网络设置。接口诊断的条目数量包含虚拟和过滤层接口，不能当作实际网卡数。未来采集所需权限将单独验证。

## 开发文档

- [产品范围](docs/product.md)：第一版要解决的问题与功能边界。
- [架构](docs/architecture.md)：项目依赖与采集、界面、存储的划分。
- [记账规则](docs/accounting.md)：去重、基线、未知归属及时间规则。
- [开发指南](docs/development.md)：构建、测试、格式检查及贡献方式。
- [任务路线](docs/roadmap.md)：阶段进度、任务编号与放行条件。
- [验收计划](docs/validation.md)：直连、代理、TUN、WLAN、SSH 等场景。
- [架构决策](docs/decisions/0001-foundation.md)：技术选择及重新评估条件。

项目沿用 [Apache License 2.0](LICENSE)。
