# 架构设计

状态：初始方案，部分基础已实现；采集、服务与存储仍待验证和开发。

## 当前项目边界

| 项目 | 职责 | 当前实现 |
|---|---|---|
| FlowLedger.Domain | 与平台无关的记账规则 | 累计计数样本、字节量、基线/重置/差分 |
| FlowLedger.Application | 应用契约与返回模型 | 环境诊断契约及不含地址的摘要 |
| FlowLedger.Infrastructure.Windows | Windows 平台实现 | 系统/运行时与接口类型、状态分组诊断 |
| FlowLedger.Collector | 未来后台采集程序的入口 | 当前为普通控制台程序，只支持帮助和只读诊断 |
| FlowLedger.Desktop | WPF 界面及组成入口 | 导航、空状态和手动环境检查 |
| FlowLedger.Domain.Tests | 记账行为回归 | 首次基线、重置、接口切换、时间、整数精度与溢出 |

引用方向：Application → Domain；Infrastructure.Windows → Application；Desktop/Collector → Application 和 Infrastructure.Windows；测试 → Domain。平台实现不能倒流进入 Domain。当前桌面外壳用少量事件处理；有真实异步采集状态后再引入对应视图模型，不预建大量空框架。

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
