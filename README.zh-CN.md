# FlowLedger · 流量簿

<img src="src/FlowLedger.Desktop/Assets/FlowLedger.png" alt="FlowLedger 标志" width="112" />

面向 Windows 的本地流量账本，计划支持按应用、不同 Wi-Fi、代理路径和 SSH 连接统计流量，并按日、周、月及自定义账期汇总。

[English](README.md) · [开发指南](docs/development.md) · [架构设计](docs/architecture.md) · [任务路线](docs/roadmap.md)

**当前处于采集与代理关联验证阶段。** 已实现接口、Wi-Fi 候选分账、ETW 应用统计和 Clash/Mihomo 代理账本。受控 QUIC、持续 TCP、异常会话恢复及本机显式代理关联已有实测；完整公网/TUN 精度和长期稳定性仍待验收。历史持久化和报表未实现。

## 已实现与计划功能

| 功能 | 当前状态 |
|---|---|
| 中文桌面：总览、网络账本、应用统计、代理账本、远程连接、运行环境 | 已实现导航；远程连接仍为空状态，没有虚构数据 |
| 所选接口的实时速率与本次运行用量 | 已实现；GetIfTable2 读取，每秒采样，关闭窗口停止且不保存历史 |
| 硬件、隧道、回环、虚拟/其他及过滤层分类 | 已实现；依据原生类型/标志，物理候选不等于已确认互联网出口 |
| 手动查询当前 Wi-Fi 名称 | 已实现；处理权限拒绝、未连接及不可用，不据此进行历史分账 |
| Wi-Fi 通知与本次运行分账 | 已实现按需启停；网络候选、边界未分配、未识别分别展示 |
| 只读出口关系检查 | 已实现路由条目摘要；不推断每个应用的互联网出口 |
| 只读系统、运行时和网络接口分类诊断 | 已实现；不读取接口名称、地址、SSID 或连接目的地 |
| 首次基线、重置、缺口、时钟变化和缺失测量规则 | 已接入实时接口会话，有自动化测试覆盖 |
| 按进程实例的 TCP/UDP、IPv4/IPv6 事件用量 | 实验性 ETW 原型；需要管理员，受控回环归属/字节实测通过，完整验收待完成 |
| Wi-Fi 持久历史及切换/漫游实机验收 | 待完成；当前分账只在内存中 |
| Clash / Mihomo 去重与原应用归属 | 已实现独立核心 API 增量与保守 TCP 关联；本机显式代理已测，完整 TUN/公网待验收 |
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

接口、Wi-Fi 和路由功能无需管理员权限；**应用 ETW 采集保守要求提升后的管理员令牌**，普通权限返回 AccessDenied，不创建会话、不自动提权。程序不安装服务、不创建数据库、不修改网络设置。接口数量包含虚拟和过滤层条目，不能当作物理网卡数。

桌面显示接口别名，启用应用采集后仅在本地显示进程文件名/PID；Wi-Fi 信息按需读取。CLI 不输出名称、进程号、稳定标识、IP/MAC、SSID、配置、服务器地址、报文内容或密钥。`--verify-counters` 夹取同层参考范围，不证明应用归属或运营商账单精度；无可比较硬件接口视为结论不足。

可选的 `--probe-wifi` 仅输出能力状态；`--watch-wifi 5` 订阅通知并持续查询五秒，输出匿名分区与字节守恒结果。两者可能触发 Windows 位置权限提示，程序不自动授予权限。完整注册 ACM/MSM 表示已订阅对应通知源，不保证每条通知都无遗漏。[候选设计决策](docs/decisions/0002-wifi-candidate-accounting.md)和[验收计划](docs/validation.md)记录了限制。

在**应用统计**页点击“开始应用统计”。若提示权限不足，先关闭软件，再手动以管理员身份打开。每个进程实例单列收发事件 size，未确认身份单列未知；停止保留记录，再次开始清空应用记录，关闭后丢失。电源状态变化后停止，需要手动重新开始。这些数字包含回环/局域网及可能重叠的代理两段，不是互联网账单，也未映射到 WLAN。

可选管理员命令 `--watch-apps 5` 输出匿名进程用量及进程资源采样；`--verify-apps` 检查本机 TCP/UDP v4/v6 回环覆盖。新增 `--validate-apps` 执行三轮独立测试进程、不等量收发、短连接和 8 路并发 TCP，用第二条会话的 TraceEvent 类型化解析及已确认 socket 送达量对照生产账本。不访问外部服务器、不捕获报文内容。受控回环及管理员界面开始/停止已通过；公网/TUN 精度、真实 PID 复用、强制原生丢失与长期验收仍待完成。两条 ETW 会话共享内核事件源，不能证明独立事件完整性。见[实机报告](docs/validation/2026-10-29-etw-loopback.md)和 [ADR 0003](docs/decisions/0003-application-etw-validation.md)。

本轮已补齐本机 QUIC v4/v6、三轮各 30 秒持续 TCP，以及采集中断和异常退出后的会话回收；新增管理员入口 `--verify-reliability 30` 和 `--verify-etw-recovery`。回收只匹配本用户、本软件的新格式会话及已退出的进程实例，跳过活跃/不明/旧格式会话。公网、TUN、强制原生丢失和长期验收仍待完成，见[本轮结论](docs/validation/2026-10-29-reliability-proxy.md)。

在**代理账本**填写 HTTP/mixed 入口端口（常见为 7890），点击“查找本机控制器”，本地输入控制器密钥，再启用观察。控制器端口与代理入口不同，核心重启后可能变化；不要把密钥发送到聊天或写进仓库。只读取 `/version`、`/connections`，不读取客户端配置、不修改规则/系统代理/TUN。密钥只留内存；停止保留记录，再启用清空，关闭后丢失。

核心逻辑总量和连续观察的连接明细分别展示；每个连接只计一次，不加上 ETW 原应用、核心进程、回环、代理链节点或网卡数字。TCP 关联要求 API 读取前后都能核对同一连接及进程实例；UDP、短连接、TUN 地址不匹配等保留未知。活动快照会漏掉新连接前段及关闭尾部，明细之和不是完整核心总量。点击“验证本机代理”可用合成 1 MiB 回环响应检查显式代理关联，不验证 TUN 或公网。见[代理设计](docs/decisions/0004-proxy-accounting.md)与[支持矩阵](docs/support-matrix.md)。

运行辅助文件仅保存本用户的随机 ETW 会话归属标识和不含密钥的合成代理验证摘要，不保存真实连接明细或流量历史。后台服务、SQLite 和日周月报表仍未实现。

## 开发文档

- [产品范围](docs/product.md)：第一版要解决的问题与功能边界。
- [架构](docs/architecture.md)：项目依赖与采集、界面、存储的划分。
- [记账规则](docs/accounting.md)：去重、基线、未知归属及时间规则。
- [开发指南](docs/development.md)：构建、测试、格式检查及贡献方式。
- [任务路线](docs/roadmap.md)：阶段进度、任务编号与放行条件。
- [验收计划](docs/validation.md)：直连、代理、TUN、WLAN、SSH 等场景。
- [架构决策](docs/decisions/0001-foundation.md)：技术选择及重新评估条件。

项目沿用 [Apache License 2.0](LICENSE)。

ETW 解析依赖 Microsoft TraceEvent 4.0.0（MIT），见[第三方声明](THIRD-PARTY-NOTICES.md)。
