# ADR 0003：明确启用的 ETW 应用统计原型

日期：2026-10-28；2026-10-29 补充实测。状态：原型及管理员受控回环验证已完成；完整覆盖/精度及持续性能仍待验收，V1-04/V1-05 不放行。

## 选择与权限

使用 Microsoft 的 `Microsoft.Diagnostics.Tracing.TraceEvent` 4.0.0（MIT）解析 Windows 经典内核事件，不自行实现全部事件结构。版本固定于 Windows 基础设施项目；Domain 和 Application 无此依赖。库会为内核网络采集同时启用 Process/Thread；程序只消费进程生命周期及指定网络事件。

应用采集由界面“开始应用统计”或 CLI `--watch-apps` / `--verify-apps` 明确启动。保守要求提升后的管理员令牌；普通用户返回 AccessDenied / 错误 5，不尝试 UAC、不改组成员、不安装服务或驱动。总览、Wi-Fi 和路由功能保持原权限要求。未来服务账户和最小权限设计另行验证。

会话使用私有名字与 NoRestartOnCreate，不占用 NT Kernel Logger，不停止其他工具的会话。当前名字为 `FlowLedger.<App/Reference>.<本用户 owner>.<PID>.<进程创建 ticks>.<采集 GUID>`；LocalApplicationData/FlowLedger/etw-owner.id 保存随机归属标识，不含流量或密钥。管理员明确启动采集时，只回收匹配该 owner 且进程确定已退出/复用的残留；活跃、不明、旧格式或其他用户的会话跳过，标识损坏则失败关闭。使用 64 MiB 请求缓冲区、实时内存消费、不创建 ETL。正常停止查询丢失、停止自己的会话、排空、合并最终丢失并释放；异常标记 Faulted/未知，不补算崩溃缺口。回收质量计数返回界面；实机中断及强制退出的自有测试辅助进程回收已验证。

## 事件与计量层

订阅 TCP Send/Recv、UDP Send/Recv 各自的 IPv4/IPv6 事件，共八类。只接受已明确布局的经典 v2 元数据，读取开头两个 UINT32：负载 PID 和 size；不使用执行回调的线程或事件头 PID。其他版本/短元数据拒收并计入质量计数；拒收事件没有可确认的字节，不能虚构其未知字节。

保留 TCP4/TCP6/UDP4/UDP6 四种口径、收发方向和整数 size。无网络事件时 Observed 为 null；有效零字节事件才构成零观测。没有连接、复制或单独重传事件的记账回调，避免额外叠加。size 是否包含特定头部、重传/卸载和 loopback 的行为必须用独立同层参考验收，不能凭字段名认定它是应用文件字节。

当前包含本机回环、局域网及可能的代理两段，既不去重也不映射到物理出口/WLAN。每个进程实例单列，不按同名文件自动合并应用；不能证明同名可执行文件属于同一产品。网卡字节、这些应用事件字节和未来代理 API 字节不能相加当作总用量。

## 进程生命周期与保守降级

会话纪元 + PID + 首次观察的整数 QPC 区分实例，内核进程键辅助匹配生命周期。真正 Start 和启动时 DCStart/rundown 分别标记；rundown 的首次观察时间不是进程真实启动时间，不把观察之前的流量追记到它。Stop 采用排他结束边界，新 Start 即使缺失前一 Stop 也关闭旧实例；键不匹配的 Stop 不关闭新实例。

无身份、观察之前/退出之后的网络事件进入未识别。任一已消费事件时间回退表明归属可能错位，清除身份并把已有实例字节保守转入未识别；后来到达的身份不追溯改名。已知 ETW 丢失增加或丢失计数查询失败也触发此处理，因为无法定位隐藏 PID 复用的边界。总观察字节保留，未知桶的增长可能包含既有行的撤回。此策略牺牲覆盖率，避免继续展示可能错误的进程名称。

最多保留 4096 个生命周期实例，达到上限后的新身份不再入表，但仍关闭发生复用的旧 PID，后续相应网络字节进入未知；质量标记保留。丢失/乱序撤销归属后清空实例表，既有字节留在未知桶。各方向、各传输类型始终满足：`已观察字节 = 实例之和 + 未识别`。这一守恒只针对已接受事件，不证明事件完整送达或互联网计费精度。

进程信息只取可执行文件末级名，不读取命令行/SID，不保留完整路径、目的地址、端口、数据报内容或 ETW 事件副本。CLI 不输出进程名称、PID 或内核键，只返回匿名行及质量摘要；桌面仅在本地显示名称/PID。

2026-10-29 修正：Windows 在 DCStart/rundown 中包含 PID 0 空闲伪进程。它不是应用生命周期，忽略此身份事件，不将其存在记为拒收；若网络事件的负载 PID 为 0，字节仍归入未知。负 PID、非空闲实例缺失名称及不支持的网络元数据仍按原有异常规则处理。首轮三次实测各有一次拒收；修复后拒收归零，第二会话每轮确认一个 idle rundown，未出现非空闲空名 rundown。依据见 [Process_TypeGroup1 说明](https://learn.microsoft.com/en-us/windows/win32/etw/process-typegroup1)。

## 界面生命周期

应用模块有独立串行门，启动、读取、停止和关闭不会并发释放。停止保留最终内存快照；再次开始建立新纪元并替换记录。收到 suspend/resume 通知时停止应用采集，需要手动重新开始；不跨睡眠继续声称完整。关闭窗口等待应用和接口模块分别清理。不实现后台运行、数据库和日周月汇总。

## 验证入口与尚欠证据

`--watch-apps <1..60>` 每秒读取健康状态并在结束后输出匿名摘要；没有数据、无权限、已知丢失/拒收、身份失效或容量不足时退出 1。

`--verify-apps` 先确认权限再产生受控本机回环流量：TCP4/TCP6 各 128 KiB，UDP4/UDP6 各 8 个 1 KiB 数据报。两端在同一进程，故预期每种传输的 socket 发送和接收字节相同。打印参考 socket 字节和该 PID 的 ETW size，检查四种协议双向可观察、归属到测试进程、守恒和质量计数。只有覆盖检查通过才退出 0；它不验证 size 与 socket 字节相等，不验证公网、QUIC、高吞吐或代理去重。无需外部服务器，不更改防火墙或网卡配置。不能运行 IPv6/回环时视为结论不足。

2026-10-28 的普通用户实测：两个入口均返回 AccessDenied 且无测量，没有运行管理员 ETW。自动化覆盖领域规则、合成元数据及假会话/界面生命周期，不能替代真实内核事件。

2026-10-29 经用户授权提升后，新增 `--validate-apps` 实现三轮独立测试进程、不等量收发、短连接及 8 路并发突发；使用第二条唯一命名会话的类型化解析（不复用生产解码/归属算法）与 socket 实际送达量对照。等待测试进程退出和两条会话排空后，逐实例、协议、方向精确比较，并要求唯一 Start/Stop、生产实例非 rundown、守恒和双方健康；无权限不创建参考或子进程。实际 48 个实例全部通过，原 `--verify-apps` 三轮亦通过。用户确认管理员桌面开始/停止后保留记录。watch 增加进程 CPU/工作集/私有内存采样，30 秒观察正常结束；详见[脱敏实测结论](../validation/2026-10-29-etw-loopback.md)。

类型化参考仍共享同一内核事件源，只交叉核对解析与归属；socket 参考仅在已测回环条件下精确一致，不据此推断公网包头、重传、卸载或计费语义。后续已补齐本机 QUIC v4/v6、三轮持续 TCP、会话中断/异常退出恢复及短时资源基线，见[本轮报告](../validation/2026-10-29-reliability-proxy.md)。真实 PID 复用、公网/代理、主动原生丢失、极限压力及长期稳定性仍待验收。

## 参考

- [Microsoft TraceEvent 源码与许可](https://github.com/microsoft/perfview/tree/v4.0.0/src/TraceEvent)
- [经典内核网络事件定义](https://github.com/microsoft/perfview/blob/v4.0.0/src/TraceEvent/Parsers/KernelTraceEventParser.cs)
- [TraceEvent 会话生命周期](https://github.com/microsoft/perfview/blob/v4.0.0/src/TraceEvent/TraceEventSession.cs)
- [TCP/IP 事件](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip)
- [StartTrace 权限](https://learn.microsoft.com/en-us/windows/win32/api/evntrace/nf-evntrace-starttracea)
