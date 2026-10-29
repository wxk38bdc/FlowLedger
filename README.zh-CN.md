# FlowLedger · 流量簿

<img src="src/FlowLedger.Desktop/Assets/FlowLedger.png" alt="FlowLedger 标志" width="112" />

面向 Windows 的本地流量账本：接口与应用流量、Wi-Fi 候选分账、Clash/Mihomo 观察、SSH 客户端连接，以及日周月报表。

[English](README.md) · [使用说明](docs/usage.md) · [开发指南](docs/development.md) · [架构](docs/architecture.md) · [任务路线](docs/roadmap.md)

**v0.2 开发预览。** 已实现本地 SQLite 历史、报表、CSV、SSH 连接统计及中英文即时切换。OpenSSH IPv4/IPv6 非标准端口本机计量、SQLite 异常退出与重放已通过验证。完整 SSH 场景、公网/TUN、网络切换及长期验收仍待完成。真实 TUN 验证暂缓；暂无后台服务和安装包。

## 功能与边界

| 功能 | 当前范围 |
|---|---|
| 接口速率与用量 | 原生 GetIfTable2，每秒采样，每张接口独立 |
| 硬件/隧道/回环/过滤分类 | 物理候选不证明互联网出口 |
| Wi-Fi 候选分账 | 明确启用通知与当前连接读取；候选、边界、未知逐字节守恒 |
| 路由关系 | 只读摘要，不推断应用出口 |
| 应用 TCP/UDP v4/v6 | 明确启用管理员 ETW，区分进程生命周期、未知及丢失 |
| ETW 可靠性 | 本机 QUIC、三轮持续 TCP、自有会话中断/崩溃恢复已测 |
| Clash/Mihomo 账本 | 核心与部分连接 API 增量分开，保守 TCP 原应用关联；显式代理已测 |
| SSH 连接账本 | 元数据候选、进程/连接实例、服务器别名、精确 IP + 端口规则；OpenSSH 非标准端口本机 v4/v6 已测 |
| SQLite 历史 | 事实与累计检查点同事务、幂等重放、无符号整数精度、质量标记及归属撤回 |
| 报表 | 日/周/月/自定义范围、时区、部分记录标记、CSV |
| 界面 | 七页导航、分层说明、简体中文/English 即时切换并保存选择 |
| 后续 | 后台服务、安装包、额度、导出隐藏名称、保留/备份管理、广泛硬件与 72 小时验收 |

接口、应用、代理与 SSH 存在重叠，**不能相加作为整机总用量**。SSH 是应用专题子集，端口转发还可能产生额外候选 TCP 腿。接口包含局域网流量，回环和虚拟接口单独保留。没有数据不表示用量为零。见[记账规则](docs/accounting.md)和[支持矩阵](docs/support-matrix.md)。

## 构建与运行

使用 Windows x64 和稳定版 **.NET 10 SDK**，不强制安装 Visual Studio。发布后的程序需要 .NET 10 Windows Desktop Runtime。

```powershell
git clone https://github.com/wxk38bdc/FlowLedger.git
cd FlowLedger
dotnet restore FlowLedger.slnx
dotnet build FlowLedger.slnx --configuration Release --no-restore
dotnet test --solution FlowLedger.slnx --configuration Release --no-build
dotnet run --project src/FlowLedger.Desktop --configuration Release --no-build
```

打开窗口即开始接口采样。Wi-Fi 分账和代理观察分别手动启用。**应用和 SSH 统计需要以管理员身份打开程序后，再点击开始**。程序不自行提权、不安装驱动/服务、不修改网络或代理设置。电源边界停止应用、代理和 SSH，需手动重新启用。

SSH 使用：先在 **SSH 连接** 页开始统计，再用 Windows 终端里的 OpenSSH 建立连接。新建出站连接及 22 端口可作为客户端候选；已有非标准端口连接或需要别名时，停止统计，在“服务器别名与规则”添加实际 IP、端口和别名，再次开始。不解析主机名。“客户端候选”基于名称，“用户规则”表示用户确认，均不是协议解析。跳板/代理只展示本机可见端点，复用通道和远端自身下载不推算到最终服务器。还识别 scp/sftp、Xshell 和 PuTTY/Plink 名称，本轮优先实测 OpenSSH。

代理使用：填写 HTTP/mixed 入口端口，点击“查找本机控制器”，本地输入密钥，再启用观察。控制器端口与入口端口不同。密钥只留内存，仅向回环 `/version`、`/connections` 发 GET，不读取客户端配置或修改规则。活动列表会漏掉短连接及关闭尾部，核心和连接量分开。内置验证产生合成 1 MiB 回环响应，不验证 TUN/公网。

## 本地历史与报表

桌面正常每五秒合批保存已观察数据，停止、查询和退出时排空，位置为：

```text
%LOCALAPPDATA%\FlowLedger\history.sqlite3
```

停止、重启或开始新会话不会清空已保存历史。进入 **历史报表**，选择一个独立账本、汇总周期、包含首尾当天的日期范围和时区。周从周一开始；按观察结束时间归账，不按比例拆分跨账期区间。空白表示没有观察。CSV 导出当前报表，包含整数收发量、来源、时区和质量信息。

关窗停止采集，未观察或未提交的流量无法恢复。保存失败会显示并重试；退出仍有未保存数据会明确提示。本地未加密数据库可能含接口/网络名、进程名及 SSH 服务器地址/别名，不上传，不含密码、代理密钥、SSH 密钥、命令行或报文内容。CSV 当前含真实名称，数据库及导出应妥善保管。自动保留、备份管理和加密存储暂未实现。见[完整用法](docs/usage.md)和[存储/SSH 设计](docs/decisions/0005-local-history-ssh-reports.md)。

## 验证

```powershell
dotnet format FlowLedger.slnx --verify-no-changes --no-restore
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --help
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --diagnose
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-history
# 明确在管理员环境运行的本机合成验证：
dotnet run --project src/FlowLedger.Collector --configuration Release --no-build -- --verify-ssh
```

普通 CLI 观察仅在内存且匿名。历史验证使用可删除的合成数据库，只终止自己的事务辅助进程。SSH 验证用已安装 OpenSSH 连接合成本机 banner/KEX 端点，禁用用户配置、身份文件/agent 和交互认证，不验证真实远端登录/文件传输。详见[本轮验证](docs/validation/2026-10-29-ssh-history.md)、[ETW 实测](docs/validation/2026-10-29-etw-loopback.md)、[可靠性/代理实测](docs/validation/2026-10-29-reliability-proxy.md)和[剩余验收](docs/validation.md)。

## 仓库与许可

Domain 保持无平台依赖；Application 定义监测/历史契约与报表规则；Windows 基础设施实现原生采集、代理与 SQLite；Desktop/Collector 负责组成。测试覆盖领域、生命周期、存储、报表、Windows 界面和互操作。见[架构](docs/architecture.md)与[贡献指南](CONTRIBUTING.md)。

项目使用 [Apache License 2.0](LICENSE)。TraceEvent、Microsoft.Data.Sqlite、SQLitePCLRaw 和 SQLite 保留各自许可，见[第三方声明](THIRD-PARTY-NOTICES.md)。不分发 Mihomo 验证运行时或数据库。
