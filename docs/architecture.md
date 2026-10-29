# 架构设计

状态：接口、Wi-Fi、管理员 ETW、Clash/Mihomo、SSH 客户端候选、本地 SQLite 和报表已实现；桌面支持中英文。OpenSSH 非标准端口本机 v4/v6 及 SQLite 异常恢复已有实测。真实 TUN 暂缓，其他未测路径/长期验收保留；后台服务未实现。

## 当前项目边界

| 项目 | 职责 | 当前实现 |
|---|---|---|
| FlowLedger.Domain | 与平台无关的记账规则 | 接口/Wi-Fi、应用生命周期、代理和 SSH 连接守恒分区 |
| FlowLedger.Application | 应用契约与会话协调 | 采集/历史契约、时区/账期报表规则 |
| FlowLedger.Infrastructure.Windows | 平台与数据适配 | GetIfTable2、Wi-Fi/路由、明确启用 ETW、代理、SQLite |
| FlowLedger.Collector | 采集验证 CLI | 匿名接口/Wi-Fi/应用观察、参考和回环覆盖验证入口 |
| FlowLedger.Desktop | WPF 界面及组成入口 | 七页中英文界面、独立采集启停、自动存储与报表/CSV |
| 测试项目 | 领域、协调器与互操作回归 | 差分、断档、隔离、ABI 布局、原生分类及读取 |

引用方向：Application → Domain；Infrastructure.Windows → Application；Desktop/Collector → Application 和 Infrastructure.Windows；各测试项目引用对应生产层。平台实现不能倒流进入 Domain。总览以 Observable ViewModel 保存选择与异步采样状态，导航仍使用少量窗口事件处理。

## 当前接口原型

WindowsInterfaceCounterReader 调用 GetIfTable2，finally 释放原生表，按 SDK MIB_IF_ROW2 布局读取身份、状态、硬件/过滤标志和 64 位字节。GUID 为接口键（缺失时仅在会话内退回 LUID）；LUID、索引形成计数连续性键，NetworkGuid 独立提供给网络归属层。分类先排除过滤层，再识别回环和隧道，硬件标志且非端点的条目标为物理候选，剩余保守标为虚拟/其他。不根据名称识别 Clash，也不自动推断默认路由或合计多出口。

InterfaceMonitor 为每个接口保留独立 InterfaceTrafficSession。每秒任务在后台读取，串行回到界面线程应用；不重叠采样。每次进程/读取器创建新计数纪元，首样本不累计启动前流量。WPF 收到电源通知时打断所有基线，并丢弃通知前尚未返回的读取结果。长间隔、失败、离线、移除和观察键变化均不能跨越计量。窗口关闭停止计时器并解除电源事件订阅。

Wi-Fi 在手动查询或用户启用分账时读取当前连接，不扫描周围网络、不取配置密钥、不改权限。WindowsWifiTrafficSource 在接口计数前后查询当前连接与通知序列，WifiLedgerCoordinator 将有效差分交给各接口的 WifiUsageLedger 分区。候选身份与限制见[设计决策](decisions/0002-wifi-candidate-accounting.md)。

WindowsWifiNotificationListener 显式订阅 ACM+MSM；失败时退回 ACM 并将归属降级为未知，完整注册也不证明通知绝无遗漏。回调只复制头部元数据并更新加锁的序列/过渡状态，不调用 WLAN API、不接触界面、不保留通知负载。委托在句柄有效期间保持引用；注销和关闭在锁外完成，避免等候回调时死锁。暂停和退出先等待正在执行的读取，再释放订阅。采样、模式切换与退出通过 SemaphoreSlim 串行化，电源通知使在途结果失效。

WindowsRouteEvidenceReader 调用 GetIpForwardTable2，只向模型返回接口 LUID 和 IPv4/IPv6 默认路由、其他前缀路由数量，不输出网关和目的地址。它用于观察接口/路由关系，不根据默认路由或条目数量推断全部应用出口；路由度量、目的地址最长前缀匹配和 VPN 路径归属仍待验证。

## 应用统计原型

ApplicationTrafficLedger 在纯领域层按 PID、整数 QPC 生命周期和进程键维护实例；四种 TCP/UDP v4/v6 口径单独守恒，缺失归属进入未知。丢失或乱序可能隐含 PID 复用，保守撤回既有进程归属到未知。容量有界，不读正在运行进程列表来猜测旧事件身份。

WindowsApplicationTrafficSource 使用固定版本 Microsoft TraceEvent 和唯一命名会话，八类网络回调读取经典 v2 PID/size。普通应用模式不保留地址；明确启用的 SSH 模式另读取 TCP 端点和连接开始/结束元数据，不读报文内容。IEtwApplicationSession 隔离真实会话与合成生命周期测试。停止先结束会话再等待消费者排空，释放源后仍可返回内存终态。连接生命周期事件只用于分行，不叠加其 size。

ApplicationMonitorViewModel 与接口模块各自串行，计时器只刷新已明确启用的应用源；停止保留快照，重新开始替换会话。电源边界停止且不自动恢复。权限失败显示原因而非零用量。完整策略、权限与未完成的实机验收见 [ADR 0003](decisions/0003-application-etw-validation.md)。

Collector 的 Validation 模块仅供明确调用的管理员验收：有界回环子进程产生不等量收发，第二条唯一命名 ETW 会话直接使用 TraceEvent 类型化 PID/size，不复用生产解码器或领域生命周期算法。等待测试进程退出及两条会话排空后，逐实例、方向及传输类型比较 socket 参考/类型化参考/生产账本。参考共享内核事件源，不能独立证明事件完整送达；不在 CI 或桌面采集中启动。公开报告只保留匿名槽位、合成参考量和质量/资源摘要。

## 后续数据流

代理模块：Domain/Proxy 的 ProxyUsageLedger 独立维护核心增量与活动连接差分、未知/溢出和连续性；Application/Proxy 定义读取契约；Windows/Proxy 实现有界只读 HTTP、TCP4/6 所属进程表及前后归属核对；Desktop 的独立视图模型串行启停/验证/关闭。API 端点仅限字面量 loopback，密钥不落盘，控制器发现不发送密钥。不把平台/HTTP 依赖引入领域层。详见 [ADR 0004](decisions/0004-proxy-accounting.md)。

ETW 会话新增每用户 owner 标识与 PID/创建时间命名；显式启用管理员采集时，只回收匹配本用户命名且 owner 确定已退出的残留。活跃、不明、旧格式会话不动。仍不实现后台守护进程或服务；恢复不能补回进程崩溃期间的流量。

```text
接口计数 / ETW / WLAN 通知 / 代理只读 API
                   ↓
        归属、计数纪元、去重与质量标记
                   ↓
             本地事实数据与聚合
                   ↓
             查询、报表与桌面展示
```

未来采集器独立于界面运行。服务与普通用户界面通过受限本机通信交换摘要；账户、权限和通信访问控制待设计。当前没有服务注册或 IPC，只有窗口内采集的本地持久化。

## 候选采集策略

- 接口：GetIfTable2 / GetIfEntry2，使用稳定接口身份、硬件/过滤接口标志和路由关系识别出口；不依赖接口名称过滤。
- 应用：ETW TCP/UDP 与进程生命周期事件。事件负载 PID 与事件头 PID 不可混用；size 语义、重传、IPv6、短连接、QUIC 必须验证。
- WLAN：Native Wi-Fi 与连接通知，处理用户会话和位置权限。通知与计数采样之间的切换不确定量需要保留。
- 代理：按实际核心版本适配旧 Clash / Mihomo，只读获取连接、流量和链路。活动连接快照不能作为全量字节真值。
- SSH：结合客户端进程生命周期、连接地址/端口和可确认的隧道关系；不把端口 22 当作唯一识别规则。

ETW 关键验收不通过时再评估 WFP 等替代方案，另行明确驱动签名、安装及维护成本。当前不依赖 Npcap、WinPcap 或自研内核驱动。

## 已实现：SSH、存储、报表与界面

SshUsageLedger 以已知客户端/用户规则为候选证据，按进程生命周期、四元组和连接实例分行，未知/容量/丢失保持守恒。WindowsApplicationTrafficSource 的可选 SSH 领域账本与普通应用统计独立启停；本机验证不使用用户 SSH 配置/身份文件或远端账号。

Application/History 定义 IHistoryStore、累计 HistoryFrame、精确区间事实和 UsageReports。Infrastructure/History 的 SqliteHistoryStore 使用 WAL/FULL、参数化 SQL、事务内事实 + 累计检查点，UInt64 十进制 TEXT，冲突/陈旧帧拒绝，重放幂等，身份撤回同步改列本纪元历史未知。Desktop 的 HistoryViewModel 负责生成独立来源快照、后台保存与可见错误/重试；SQLite 不进入 Domain。

采集继续每秒观察，存储正常五秒合批并保留质量，失败帧不覆盖待写有效测量；停止/查询/退出立即排空。报表以独立 WAL 读取快照查询同一事实，按观察结束 UTC 转换选定时区的日/周/月或自定义范围，不按比例拆分跨期字节。应用按名聚合，Wi-Fi 保留候选身份，来源/层级始终分开。CSV 带来源、区间、时区、整数及质量元数据，对公式开头的名称添加文本前缀。

WPF 七页将常用动作/数字置前、说明折叠。UiText 保留动态状态的双语值，UiCatalog 提供静态资源，TextExtension 显式绑定表格列标题，语言选择在数据库 settings 中保存。单实例桌面减少重复窗口；没有后台采集。具体数据模式、时区语义和限制见 [ADR 0005](decisions/0005-local-history-ssh-reports.md)。

进程身份包含启动时间。记录采集断档、ETW 丢失、API 断联、网络身份缺失和重置。监测失败不能静默成为零用量。速度用单调时钟，展示账期使用选定时区。

## 参考

- [Windows 接口计数与属性](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_if_row2)
- [ETW TCP](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip)、[UDP](https://learn.microsoft.com/en-us/windows/win32/etw/udpip)、[丢失计数](https://learn.microsoft.com/en-us/windows/desktop/ETW/event-trace-properties)
- [Wi-Fi 权限](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes)
- [Mihomo API](https://wiki.metacubex.one/api/)
