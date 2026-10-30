# Nowhere 验证记录：2026-10-30

> 更新说明（2026-10-30）：本文保留原 Windows 阶段说明与当时验收记录；当前已增加 macOS arm64 实现、完整 bundle 更新回滚和发版脚本。当前完成/待验收状态以 [实施进度](nowhere-progress.md) 和 [Mac 验收清单](nowhere-macos-acceptance.md) 为准，历史平台限制不代表当前能力。

状态：Windows x64 开发实现；不是正式版验收签字。

## Windows 完整流程交付补充

按用户最新要求，本轮收敛为 Windows x64 完整操作逻辑，先交付用户手动验证可用性，之后再继续完整网络互通验收。

- `LoadCore` 明确返回启动结果；桌面和 WebAPI 不再把未成功启动的会话报告为运行中。
- 系统代理应用与启停使用同一生命周期锁；取消、失败或用户停止后不会将系统代理指向失效入口，也不由旧重载任务自动恢复连接。
- 参数预检失败时保留旧连接并明确提示新配置尚未生效；清理仅处理当前会话持有的代理和进程。
- 退出先停止会话，再保存配置和统计，避免保存错误跳过进程清理。
- `scripts/Publish-NowhereWindows.ps1` 生成自包含 Windows 包，包含 .NET 运行时、固定版本 Nowhere/Xray/sing-box、libcronet、Wintun 及 15 项默认规则，全部下载资产固定 SHA-256。
- 本轮 ServiceLib 全量逻辑测试 **203/203 通过**；设置 `SINGBOX_TEST_EXE`，新增 TUN 配置离线检查实际执行并通过。本轮未重复运行外部 Vector 的 100 次进程测试或公网流量验证，之前的真实进程证据见下表。

新包解压运行 `v2rayN.exe`，操作步骤见 `nowhere-windows-quickstart.md`。以下原始记录保留为前一轮验证证据。

## 环境和构建

- Windows 11 Education `10.0.26200`，x64；.NET SDK `10.0.403`。
- v2rayN 基线：`57f17d5939e88c8e643ab49b90869d637765e75a`。
- Nowhere 源码基线：`a89e9896b6ab818902f2b9bad58b050ea6ad93e5`；运行验证使用官方 `v3.2.0 windows/x86_64`。
- 官方 Nowhere 可执行文件 SHA-256：`36b90a7eabbe530d508ec0c941f60c22d8decf7177f0b08261c13c81cdc0490c`。
- Xray `26.10.29`，`0afec66`，Windows amd64。测试不修改系统代理或 TUN，不使用现有 10808 入口作为测试监听器。

## 已执行

| 验证 | 结果 | 证据与边界 |
| --- | --- | --- |
| ServiceLib 全量测试 | 201/201 通过，0 失败/跳过 | 设置 `NOWHERE_TEST_EXE` 和 `XRAY_TEST_EXE`，实际进程用例已执行；最终约 39 秒 |
| WebAPI 全量测试 | 388/388 通过，0 失败/跳过 | 包含订阅身份变化后的重连事件与标记消费 |
| WPF / Avalonia 构建 | 均成功 | 最终各 0 警告、0 错误；未进行完整交互视觉验收 |
| Windows x64 Release publish | 成功 | framework-dependent；运行需要 .NET 10 Desktop Runtime |
| 官方 Vector → 本机 Portal → TCP/UDP echo | 8/8 业务组合通过 | TLS/QUIC × Morph 0/1 × TCP/UDP；本机自签证书，从受控 Portal 日志核对 pin |
| Xray → Vector → Portal → TCP/UDP echo | 8/8 业务组合通过 | 相同矩阵；Python 脚本验证桥接路线，C# 另验证生成的 Xray 配置可启动和协商 SOCKS |
| 错误 pin/key/Morph、缺失信任的自签证书 | 8/8 拒绝业务连接 | TLS/QUIC 两种承载；不把 Vector SOCKS 监听成功视为业务成功 |
| Vector 连续生命周期 | 100 次通过 | 每次创建、SOCKS 就绪、停止、检查进程退出及端口可重绑；不等同于 GUI 100 次切换 |
| 崩溃/归属/取消 | 通过 | 前置崩溃清理 Vector、旧回调不影响新实例、子实例清理、用户停止取消排队更新 |
| 官方包安装/更新/回退 | 实际执行通过 | 固定 ZIP/EXE 摘要；暂存、原子替换、旧包回退；篡改包/备份不覆盖有效目标 |
| SQLite 首次快照 | 通过 | 真实独立 WAL 数据库在线备份，首次快照保持不变 |
| 差异格式检查 | 通过 | `git diff --check` |

机器可读网络结果：

- `tests/integration/nowhere-loopback-results.json`
- `tests/integration/nowhere-xray-loopback-results.json`

## 复现

在仓库根目录运行，使用 .NET 10 SDK：

```powershell
$env:NOWHERE_TEST_EXE = 'C:\path\nowhere.exe'
$env:XRAY_TEST_EXE = 'C:\path\xray.exe'
dotnet run --project v2rayN/ServiceLib.Tests/ServiceLib.Tests.csproj
dotnet run --project v2rayN/v2rayN.WebAPI/Tests/v2rayN.WebAPI.Tests.csproj
dotnet build v2rayN/v2rayN/v2rayN.csproj
dotnet build v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj
dotnet publish v2rayN/v2rayN/v2rayN.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o artifacts/windows-x64
python tests/integration/nowhere_loopback.py --exe $env:NOWHERE_TEST_EXE --failures
python tests/integration/nowhere_loopback.py --exe $env:NOWHERE_TEST_EXE --xray $env:XRAY_TEST_EXE
```

未设置外部二进制环境变量时，进程测试函数会提前返回；不能据此声称已执行真实进程验证。本次最终运行已设置两个变量。

## 未完成的发布门槛

- 与实际订阅生产端/其他客户端确认 `sni/pin` fork 扩展；系统 CA 正向信任与公网 Portal 验证。
- 干净 Windows 环境完整 GUI 操作，系统代理、规则/DNS、实际 TUN、IPv6 与网络切换/休眠恢复。
- GUI 连续 100 次节点切换，批量测速完整业务与取消场景，至少 24 小时真实会话。
- Linux x64/arm64、macOS arm64 的安装/更新/权限与实机网络验证；本实现暂不开放这些平台的 Nowhere 运行能力。
- 正式发布包、fork 品牌与第三方依赖/对应源码交付审查。

上述事项需要真实目标环境与持续运行证据，尚未满足。P0 的本机技术路线已验证，P1–P4 已交付代码和自动测试，但计划的完整阶段验收尚未通过；P5 未完成。
