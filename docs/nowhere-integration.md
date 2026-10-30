# Nowhere 原生节点适配：开发构建说明

> 更新说明（2026-10-30）：本文保留原 Windows 阶段说明与当时验收记录；当前已增加 macOS arm64 实现、完整 bundle 更新回滚和发版脚本。当前完成/待验收状态以 [实施进度](nowhere-progress.md) 和 [Mac 验收清单](nowhere-macos-acceptance.md) 为准，历史平台限制不代表当前能力。

更新：2026-10-30。当前为 Windows x64 开发实现，尚未达到开发计划的正式发布验收门槛。

## 固定基线与范围

| 组件 | 基线 |
| --- | --- |
| v2rayN | `57f17d5939e88c8e643ab49b90869d637765e75a` |
| Nowhere 源码 | `a89e9896b6ab818902f2b9bad58b050ea6ad93e5` |
| 自动安装内核 | 官方 `v3.2.0`，Windows x64 MSVC |
| 本次运行平台 | Windows x64；其他平台明确拒绝安装及运行 Nowhere |

已有协议枚举编号不变；新增 `EConfigType.Nowhere=15`、`ECoreType.Nowhere=31`。运行路径是现有 Xray/sing-box 前置入口、分流与 DNS → 独立回环 SOCKS5 → 官方 Vector → Portal。原始节点保留真实 Portal 地址；本地端口仅属于运行实例，不覆盖远端节点，不导出。

## 链接契约

```text
nowhere://0123456789abcdef0123456789abcdef@relay.example:443?net=udp&morph=1#Example
```

| 字段 | 本 fork 的规则 |
| --- | --- |
| key | `Password` 保存 32 位小写十六进制**文本**；解码一次，不当作 UUID 或二进制 key |
| host/port | DNS、IPv4 或括号 IPv6；必须显式提供 1–65535 端口；拒绝通配和零地址 |
| net | `tcp`（默认）或 `udp`，分别代表 TLS 和 QUIC 承载；两者均可承载 TCP/UDP 业务 |
| morph | `0`/`1`，默认 `1` |
| sni | 可选 ASCII DNS 名；空值或 `none` 使用远端主机名 |
| pin | 可选 64 位小写证书 SHA-256；空值或 `none` 使用正常 CA/主机名验证 |
| fragment | 一次 UTF-8 解码的显示名，不进入服务启动 URL |

**sni/pin 是本 fork 明确定义的分享扩展**：固定上游的分享文档只列出 net/morph，不能宣称其他 Nowhere 客户端已经接受该扩展。Vector 服务配置已有相应字段。本实现不会丢弃 pin 后连接，也没有通过关闭证书验证实现兼容。自签证书指纹应通过可信渠道核对。

scheme 不区分大小写；查询字段名和值区分大小写。已识别重复参数以首值为准，首值非法即失败。未知参数、`rate/etar`、用户指定 `in` 均拒绝；高级限速参数本版不支持、不静默丢弃。URL 不接受任何路径（包括末尾 `/`）、密码分隔部分、控制字符、错误百分号或非法 UTF-8。全 URL 的百分号语法均须有效，后续重复值也不例外。组件只解码一次；`+` 保留字面含义，不作为空格。

参数持久化于 `ProtocolExtraItem.NowhereCarrier/NowhereMorph/NowherePin`，SNI 使用既有 `Sni` 字段。服务 URL 使用结构化 URI 构造，进程通过 `ArgumentList` 直接传入，无 shell。共享 key 仍可能被本机进程参数查看工具读取；日志进行脱敏不等于隐藏操作系统命令行。

## 导入、订阅与编辑

使用既有剪贴板/二维码链接入口导入；通过正式节点编辑器修改承载、Morph、SNI 和 pin，使用原有分享功能导出。普通明文及 Base64 链接列表支持混合 Nowhere/VLESS 等协议；专有 JSON、Clash YAML 不属于此链接契约。

混合链接订阅先完整解析、校验、去重，再用 SQLite 事务替换订阅行。失败保留旧节点，不提交半份内容。名称不进入 Nowhere 连接身份；host、port、key、承载、Morph、SNI、pin 都参与比较。没有生产端稳定 ID，key/地址/pin 轮换按新身份处理，不能保证识别为旧节点。活动节点身份消失时清除活动选择并提示重连，不让新参数冒充仍运行的旧快照。

## 安装、版本与恢复

第一次运行且缺失内核时，从下面固定官方地址下载；订阅无法指定下载地址、命令或可执行路径：

```text
https://github.com/NodePassProject/Nowhere/releases/download/v3.2.0/nowhere-x86_64-pc-windows-msvc.zip
SHA-256: bdc01f2df2ec8c7e01c7d994ab78e0e1977f9e278e396634aac206d8cefded82
```

该摘要来自 GitHub 官方 release API 的资产 digest，并固化于应用代码。信任边界是 GitHub HTTPS 与本 fork 发布清单；本次没有上游签名验证，不把同源摘要宣称为独立真实性证明。当前锁定版本不自动跟随 latest；更新内核需维护并审查应用内清单。

下载更新先在旧代理仍可用时完成暂存、摘要及 ZIP 布局验证，再停止受管理核心并安装；取消或下载失败不会先中断旧连接。暂存包使用 disposable 生命周期，安装前重新核验，避免把下载成功等同于更新完成。

安装器只接受根目录唯一 `nowhere.exe`，拒绝其他路径、重复项、链接及超限文件；先校验 ZIP 摘要，再暂存解压并执行 `--version`，要求 `nowhere-v3.2.0 windows/x86_64`，最后原子替换。受管理路径通常为 `<应用目录>/bin/nowhere/nowhere.exe`；使用 LocalAppData 配置时以实际应用数据目录为准。文件占用或校验失败保留旧核心。旧文件保留为 `nowhere.exe.previous`，WPF/Avalonia 的统一“检查更新”窗口提供 Nowhere 选项：“仅检查”验证当前内核是否匹配应用内置受信版本，“检查并更新”经双哈希验证和安全安装完成安装/更新。遵循“通过代理更新”开关；更新会停止相关进程并由统一流程重新连接。帮助菜单不再单独列出 Nowhere 安装/回退；保留 `NowhereInstaller.RollbackAsync` 与 `CoreManager.UpdateNowhereAsync` 回退 API。新的上游版本须先纳入应用受信清单，不会仅凭最新标签下载未验证二进制。

手工离线安装也应使用 `NowhereInstaller.InstallAsync(packagePath, managedDirectory)` 校验固定官方包，而不是把未经验证的可执行文件覆盖到目录。启动前先核对可执行文件 SHA-256 `36b90a7eabbe530d508ec0c941f60c22d8decf7177f0b08261c13c81cdc0490c`，再核对固定版本；该摘要从上述已认证 ZIP 内的官方文件提取。修改版、自行编译版或不同版本会拒绝启动，开发者须另行审查维护清单，不能仅伪造版本输出通过。回滚也先核验上一文件的固定摘要，再执行版本检查；不可信备份不会覆盖当前文件。再次连接仍受固定摘要限制，未来允许回滚到其他版本须显式保留对应可信清单。

### 首次配置快照与降级

首次通过 SQLiteHelper 写入 Nowhere 节点前，自动使用 **SQLite 在线备份 API** 创建配置目录内、与 `guiNDB.db` 同目录的 `guiNDB.db.pre-nowhere.bak`（通常 `<应用目录>/guiConfigs/guiNDB.db.pre-nowhere.bak`，LocalAppData 模式为 `%LOCALAPPDATA%/v2rayN/guiConfigs/guiNDB.db.pre-nowhere.bak`）。该备份包含 WAL 中已提交数据，后续 Nowhere 写入不覆盖首次快照；备份失败会阻止对应写入。备份包含共享 key，按原配置同等保密管理。

旧版本不认识新增枚举，不能直接降级后继续使用含 Nowhere 的数据库。建议升级前额外完整备份配置；恢复时退出客户端及相关后台实例，保留当前 `guiConfigs` 整目录副本，再将在线备份恢复为 `guiNDB.db`，处理停机后的旧 `-wal/-shm`，然后用旧版本启动并重新选择原有节点。在线数据库不能只复制主 `.db` 文件；不确定停机状态时使用应用备份或 SQLite backup API。首次快照只覆盖数据库，不回退之后修改的 JSON 配置，应用设置需从额外备份恢复。

## 启停与测速边界

每个运行/测速实例使用独立回环端口，启动进行 SOCKS5 协商就绪检查，失败清理本次创建的进程；只停止持有的实例，不按全局进程名杀进程。SOCKS 就绪只表示入口可用，远端认证和实际目标可达仍须业务验证。切换/停止取消旧启动，Vector 退出会关闭对应前置实例。

真实代理延迟和下载测速使用临时 Vector 与前置内核，支持取消与资源回收。TCP 端口探测不等于端到端成功，QUIC 的远端 UDP 承载也不能用 TCP 探测结果代表。前置内核为流量统计来源，不与 Vector 本地统计相加；原生 IPC 尚未接入。

## 验证证据与待验收项

已执行的实际本机 Portal/Vector 回环测试：TLS/QUIC × Morph 0/1 × TCP/UDP 业务全部通过；同样矩阵经过 Xray SOCKS 前置桥接全部通过。使用本机生成证书及可信本地 pin，不涉及公网 Portal 或系统代理/TUN 修改。记录见：

- `tests/integration/nowhere-loopback-results.json`
- `tests/integration/nowhere-xray-loopback-results.json`
- `tests/integration/nowhere_loopback.py`

官方 Windows v3.2.0 内核的负向业务测试也已执行：TLS 和 QUIC 各自拒绝错误 pin、错误 key、Morph 不匹配和未信任自签证书，共 8 个拒绝场景。该证据不代表 CA 成功信任路径或公网环境已经验收。

实际 Vector 进程连续创建/停止 100 次测试已通过，检查本测试持有的进程和监听均回收（`NowhereRuntimeTests.VectorLifecycle100Cycles_LeavesNoOwnedProcessOrListener`）；这是进程生命周期测试，不代替 GUI 节点切换或系统代理/TUN 切换验收。

已实测官方 ZIP 摘要匹配、安装、再次替换、回滚。单元测试覆盖链接往返、编码与非法输入、订阅事务/身份、进程就绪与取消、安装包边界和首次 WAL 在线备份。编译成功、SOCKS 就绪、本机业务互通是不同证据。

**初次记录时尚未验收（后续公网/CA 与许可工作已有新证据，见当前进度）**：干净 Windows GUI 操作、系统代理、实际 TUN 无回环、CA 信任路径、公网远端、休眠/网络变化、GUI 连续切换、24 小时稳定性、各平台实机运行及完整许可证/品牌发布材料。因此本构建不标记“Windows 正式适配完成”。其他平台不能仅以编译通过认定支持；任意策略组、链式多跳也不在当前承诺范围。

```powershell
dotnet run --project v2rayN/ServiceLib.Tests
dotnet build v2rayN/v2rayN/v2rayN.csproj
dotnet build v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj
python tests/integration/nowhere_loopback.py --exe <nowhere.exe>
```

需要 .NET 10 SDK，回环脚本需要 Python。测试产生临时密钥和进程，只应清理自身实例。对外分发仍须保留 GPLv3 通知、提供匹配源码及必要构建材料，并明确第三方 fork 身份。
