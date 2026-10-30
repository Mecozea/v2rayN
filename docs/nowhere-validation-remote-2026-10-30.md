# Windows 回归与公网节点验证

执行：2026-10-30 18:54–18:59（UTC+8）。包含编写公网验证脚本和整理结果约 5 分钟，各项执行耗时合计约 111 秒；部分项目并行执行。

## 结果与耗时

| 项目 | 结果 | 执行耗时 |
| --- | --- | --- |
| ServiceLib 全量测试 | 203/203 通过，外部 Vector/Xray/sing-box 环境变量均已设置；包含 100 次进程回收与 TUN 配置离线检查 | 43.6 秒，含构建 |
| WebAPI 全量测试 | 388/388 通过 | 20.9 秒，含构建 |
| 12 个公网节点，独立 Vector | HTTPS、每节点 1 MiB 下载、UDP DNS 均 12/12 通过 | 18.6 秒，3 并发 |
| 12 个公网节点，Xray → Vector | 同上，各项均 12/12 通过 | 18.5 秒，3 并发 |
| 本地 Xray → Vector → Portal 矩阵 | TLS/QUIC × Morph 0/1 × TCP/UDP 8 项业务组合通过；错误 key/pin/Morph、自签证书不受信任的 8 个负向场景均拒绝 | 9.7 秒 |

公网测试通过只读 SQLite 连接获取用户当前导入的 12 个 Nowhere 节点快照，未修改节点、订阅、系统代理和路由。凭据仅在内存和受控进程参数中使用，结果文件不保存共享 key、完整分享/启动 URL 或订阅地址。每个任务分配独立回环端口并清理自身进程，不停止用户已运行的客户端或内核。

公网节点均按原配置使用 TCP/TLS 承载、Morph=1、系统 CA 验证，未使用 pin 或关闭证书验证。公网测试不代表这些服务器支持 QUIC；QUIC 的 TCP/UDP 业务证据来自本地矩阵。

HTTPS 目标：`https://www.gstatic.com/generate_204`；下载目标：`https://speed.cloudflare.com/__down?bytes=1048576`；UDP 目标：`1.1.1.1:53`，查询 `example.com`，检查返回事务 ID、响应标记、rcode 和答案数。1 MiB 下载证明有效载荷转发成功，不作为持续吞吐基准。

截图中失败的美国 02（`us4.avaulon.cc:25215`）本轮两条路径均成功，没有复现该次失败，不能据此确定此前失败的原因。截图内 SOCKS 请求 EOF 也可能来自只完成协商就关闭的就绪探针，不能仅凭该日志认定远端认证失败。

## 证据位置

本工作区 `artifacts/validation-2026-10-30/`：

- `summary.json`：本轮汇总与未执行范围。
- `remote.json`：公网独立 Vector 逐节点结果。
- `remote-xray.json`：公网 Xray 桥接逐节点结果。
- `loopback-xray.json`：本地 TLS/QUIC 矩阵与认证失败测试。

复现入口：`tests/integration/nowhere_remote.py`、`tests/integration/nowhere_loopback.py`。公网脚本需显式传入只读数据库路径、官方 Vector 可执行文件和结果路径；`--xray` 可加入前置内核。

本轮未执行：系统代理切换、实际 TUN 路由接管、24 小时稳定性和 macOS 运行。不能把这轮回归称为所有正式验收项均已完成。

## Apple Silicon macOS 状态

目标为 `osx-arm64`。Avalonia 编辑界面、共享节点模型、链接与订阅逻辑已有实现；当前 Nowhere 安装清单和能力开关仍只支持 Windows x64，因此尚无可用的 Nowhere macOS 包。

已核实官方 `v3.2.0` 提供 `nowhere-aarch64-apple-darwin.tar.gz`，资产 SHA-256 为 `bf1f348eb65498d69b390066b567d3ff4a20b1cba04db13fe1e08dba8cd86f79`。协议内核本身有 Apple Silicon 发布物，无需在 C# 重写协议。

尚需实现/验证：平台化安装清单与 tar 解包、可执行权限、Unix 进程终止、macOS 系统代理与 TUN 提权、应用包及签名/隔离处理、更新回退和 arm64 真机验收。普通代理开发版估计 1–2 个工作日；连同 TUN、更新和真机回归估计 3–5 个工作日，取决于可用 Mac 环境和提权/签名问题。此为工程估算，不是已完成能力。
