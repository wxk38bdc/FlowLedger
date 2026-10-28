# 架构设计

状态：已实现接口、Wi-Fi 候选分账和明确启用的 ETW 应用统计原型。应用采集需要管理员权限，实机覆盖/精度仍待验证；后台服务与持久化待开发。

## 当前项目边界

| 项目 | 职责 | 当前实现 |
|---|---|---|
| FlowLedger.Domain | 与平台无关的记账规则 | 接口/Wi-Fi 会话、应用生命周期与未知守恒分区 |
| FlowLedger.Application | 应用契约与会话协调 | 接口/Wi-Fi 协调、应用快照及异步采集生命周期契约 |
| FlowLedger.Infrastructure.Windows | Windows 平台实现 | GetIfTable2、Wi-Fi/路由、明确启用的内存 ETW 会话 |
| FlowLedger.Collector | 采集验证 CLI | 匿名接口/Wi-Fi/应用观察、参考和回环覆盖验证入口 |
| FlowLedger.Desktop | WPF 界面及组成入口 | 总览、Wi-Fi 账本、独立应用原型与远程连接空状态 |
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

WindowsApplicationTrafficSource 使用固定版本 Microsoft TraceEvent 和唯一命名会话，八类网络回调只读经典 v2 元数据的 PID/size，不持有事件副本、地址或报文内容。IEtwApplicationSession 隔离真实会话与合成生命周期测试。停止先结束会话再等待消费者排空，释放源后仍可返回内存终态。

ApplicationMonitorViewModel 与接口模块各自串行，计时器只刷新已明确启用的应用源；停止保留快照，重新开始替换会话。电源边界停止且不自动恢复。权限失败显示原因而非零用量。完整策略、权限与未完成的实机验收见 [ADR 0003](decisions/0003-application-etw-validation.md)。

## 后续数据流

```text
接口计数 / ETW / WLAN 通知 / 代理只读 API
                   ↓
        归属、计数纪元、去重与质量标记
                   ↓
             本地事实数据与聚合
                   ↓
             查询、报表与桌面展示
```

未来采集器独立于界面运行。服务与普通用户界面通过受限本机通信交换摘要；具体账户、权限及命名管道访问控制在采集验证后实现。当前没有服务注册、IPC 或持久化。

## 候选采集策略

- 接口：GetIfTable2 / GetIfEntry2，使用稳定接口身份、硬件/过滤接口标志和路由关系识别出口；不依赖接口名称过滤。
- 应用：ETW TCP/UDP 与进程生命周期事件。事件负载 PID 与事件头 PID 不可混用；size 语义、重传、IPv6、短连接、QUIC 必须验证。
- WLAN：Native Wi-Fi 与连接通知，处理用户会话和位置权限。通知与计数采样之间的切换不确定量需要保留。
- 代理：按实际核心版本适配旧 Clash / Mihomo，只读获取连接、流量和链路。活动连接快照不能作为全量字节真值。
- SSH：结合客户端进程生命周期、连接地址/端口和可确认的隧道关系；不把端口 22 当作唯一识别规则。

ETW 关键验收不通过时再评估 WFP 等替代方案，另行明确驱动签名、安装及维护成本。当前不依赖 Npcap、WinPcap 或自研内核驱动。

## 存储与运行可靠性（待开发）

SQLite 事务批写，保留来源、层级、时间范围、计数纪元、接口/网络/进程身份和质量事件。批次幂等，异常退出和重启不能重复入账；明细与日汇总使用同一口径。高基数字段和诊断日志限量保存。

进程身份包含启动时间。记录采集断档、ETW 丢失、API 断联、网络身份缺失和重置。监测失败不能静默成为零用量。速度用单调时钟，展示账期使用选定时区。

## 参考

- [Windows 接口计数与属性](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_if_row2)
- [ETW TCP](https://learn.microsoft.com/en-us/windows/win32/etw/tcpip)、[UDP](https://learn.microsoft.com/en-us/windows/win32/etw/udpip)、[丢失计数](https://learn.microsoft.com/en-us/windows/desktop/ETW/event-trace-properties)
- [Wi-Fi 权限](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wi-fi-access-location-changes)
- [Mihomo API](https://wiki.metacubex.one/api/)
