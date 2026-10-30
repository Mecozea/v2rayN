# Windows Nowhere 使用步骤

> 更新说明（2026-10-30）：本文保留原 Windows 阶段说明与当时验收记录；当前已增加 macOS arm64 实现、完整 bundle 更新回滚和发版脚本。当前完成/待验收状态以 [实施进度](nowhere-progress.md) 和 [Mac 验收清单](nowhere-macos-acceptance.md) 为准，历史平台限制不代表当前能力。

本次交付目标是完整 Windows x64 操作流程；由用户先验证是否可用，再安排完整网络互通验收。

## 启动与添加

1. 将完整 ZIP 解压到独立目录，运行 `v2rayN.exe`。不要只复制 EXE。包中包含 .NET 运行时、Xray、sing-box、Nowhere、驱动依赖及默认规则文件。
2. 使用“添加 Nowhere 服务器”，或从剪贴板/二维码导入 `nowhere://` 链接；普通明文和 Base64 订阅列表也支持 Nowhere。
3. 填入服务器地址、端口和 32 位小写十六进制共享 key，承载选 TCP/TLS 或 UDP/QUIC，Morph 与服务器保持一致。
4. 有受信任 CA 证书时可留空 pin；自签证书须填入经可信渠道核对的 64 位小写 SHA-256 pin。需要覆盖服务器名称时填写 SNI。
5. 保存后选择节点并设为活动服务器。客户端自动管理 Nowhere 进程，无需单独启动 Vector。

示例结构（替换为你的实际参数）：

```text
nowhere://0123456789abcdef0123456789abcdef@relay.example:443?net=udp&morph=1&pin=<64位证书指纹>#我的节点
```

## 连接、切换和测速

- 普通代理：使用原有系统代理菜单，选择自动配置系统代理。失败时查看底部日志与提示，再修改节点或重新连接。
- TUN：使用管理员权限运行客户端，再开启 TUN；客户端使用 sing-box 入口并保护 Nowhere 的真实远端和进程。TUN 所需程序和驱动已随包提供。
- 节点切换：按原有方式选择 Nowhere/VLESS 等节点；客户端清理上一会话持有的进程并建立新会话。
- 测速：使用原有真实延迟、下载和批量测速菜单。Nowhere 测速实例使用独立端口，按 Esc 可取消；TCP 端口探测不等同于完整代理连接成功。
- 本地 SOCKS 就绪表示入口已启动，实际服务可达性由用户业务请求和真实延迟结果确认。

## 内核与配置

- 缺失 Nowhere 时，客户端会按内置可信清单获取固定官方版本。更新菜单另有 Nowhere 安装/修复和回退入口。
- 修复先完成下载校验，再停止受管理连接并替换；用户主动停止后不会由修复操作自动重连。正在进行 Nowhere 测速时，请先取消测速。
- 首次保存 Nowhere 节点前自动创建 `guiConfigs/guiNDB.db.pre-nowhere.bak`；已有节点和配置不会迁移到其他应用目录。
- 退出客户端清理本客户端创建的进程；不按进程名称终止用户独立启动的 Nowhere。

## 交付范围

优先提供 Windows x64 完整操作逻辑及可直接运行的包。GUI 实际使用、系统代理/TUN、公网 Portal 和长时间会话的最终结果由后续验收确认；本轮另外实现 macOS Apple Silicon，待真机验收。

这是基于上游修改的本地开发构建，并非上游官方发布。详细链接规则和恢复说明见 `nowhere-integration.md`，对应实现见随交付提供的源码快照。

重新打包需要 .NET 10 SDK：

```powershell
pwsh -File scripts/Publish-NowhereWindows.ps1 -OutputDirectory artifacts/windows-ready
```

脚本固定所有内核版本和摘要、附带默认规则及依赖，并生成自包含 Windows 包；已有非空输出目录会被拒绝，避免覆盖配置。需要代理下载时使用 `-Proxy socks5h://127.0.0.1:10808`。
