# 架构设计

状态：已实现接口采集验证原型；应用归属、后台服务与持久化仍待验证和开发。

## 当前项目边界

| 项目 | 职责 | 当前实现 |
|---|---|---|
| FlowLedger.Domain | 与平台无关的记账规则 | 累计计数、独立接口会话、断档、重置和单调速率 |
| FlowLedger.Application | 应用契约与会话协调 | 计数/Wi-Fi 契约、独立接口生命周期、只读诊断 |
| FlowLedger.Infrastructure.Windows | Windows 平台实现 | GetIfTable2、Native Wi-Fi 按需查询及匿名环境摘要 |
| FlowLedger.Collector | 采集验证 CLI | 有限时长匿名采样、同层参考校验、Wi-Fi 能力和环境诊断 |
| FlowLedger.Desktop | WPF 界面及组成入口 | 实时总览、选择接口、按需 Wi-Fi 查询及规划功能空状态 |
| 测试项目 | 领域、协调器与互操作回归 | 差分、断档、隔离、ABI 布局、原生分类及读取 |

引用方向：Application → Domain；Infrastructure.Windows → Application；Desktop/Collector → Application 和 Infrastructure.Windows；各测试项目引用对应生产层。平台实现不能倒流进入 Domain。总览以 Observable ViewModel 保存选择与异步采样状态，导航仍使用少量窗口事件处理。

## 当前接口原型

WindowsInterfaceCounterReader 调用 GetIfTable2，finally 释放原生表，按 SDK MIB_IF_ROW2 布局读取身份、状态、硬件/过滤标志和 64 位字节。GUID 为接口键（缺失时仅在会话内退回 LUID）；LUID、NetworkGuid、索引共同形成连续性观察键，不把它当作永久 Wi-Fi 身份。分类先排除过滤层，再识别回环和隧道，硬件标志且非端点的条目标为物理候选，剩余保守标为虚拟/其他。不根据名称识别 Clash，也不自动推断默认路由或合计多出口。

InterfaceMonitor 为每个接口保留独立 InterfaceTrafficSession。每秒任务在后台读取，串行回到界面线程应用；不重叠采样。每次进程/读取器创建新计数纪元，首样本不累计启动前流量。WPF 收到电源通知时打断所有基线，并丢弃通知前尚未返回的读取结果。长间隔、失败、离线、移除和观察键变化均不能跨越计量。窗口关闭停止计时器并解除电源事件订阅。

Wi-Fi 仅在明确查询时读取当前连接，不扫描周围网络、不取配置密钥、不改权限。名称只显示带查询时间的结果，不写入接口账本；切换所选网卡、接口离线或观察键变化会清除旧结果。无网络通知前不声称提供完整 SSID 分账。

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
