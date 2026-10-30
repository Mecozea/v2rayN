# v2rayN · Nowhere edition

基于 [2dust/v2rayN](https://github.com/2dust/v2rayN) 的分支，为桌面客户端增加 [Nowhere](https://github.com/NodePassProject/Nowhere) 协议支持。保留上游 Xray、sing-box 及其他协议功能。

[![Release](https://img.shields.io/github/v/release/Mecozea/v2rayN?include_prereleases&logo=github)](https://github.com/Mecozea/v2rayN/releases)
[![Build](https://github.com/Mecozea/v2rayN/actions/workflows/nowhere-release.yml/badge.svg)](https://github.com/Mecozea/v2rayN/actions/workflows/nowhere-release.yml)

## 下载

请从 **[本分支 Releases](https://github.com/Mecozea/v2rayN/releases)** 下载。上游原版不包含此分支的 Nowhere 集成。

| Nowhere 构建 | 文件 | 说明 |
| --- | --- | --- |
| Windows x64 | `v2rayN-windows-64.zip` | 自包含运行时，内置 Nowhere、Xray、sing-box 和默认规则 |
| macOS Apple Silicon | `v2rayN-macos-arm64.dmg` | M1/M2/M3/M4 等，macOS 13.6+，拖入 Applications 后启动 |
| macOS Apple Silicon 更新包 | `v2rayN-macos-arm64.zip` | 完整 `.app` bundle，供客户端事务更新使用；日常首次安装使用 DMG |

Nowhere 集成目前面向上述两个目标。上游其他平台仍保留代码，但不代表本分支已完成对应平台的 Nowhere 验证。macOS 构建由 Actions 真正运行原生内核及本地回环测试；系统代理、TUN 和实际服务器连接仍需 Mac 真机验收。

macOS 测试包使用 **ad-hoc 签名，尚无 Apple Developer ID 签名和公证**。首次启动可能被 Gatekeeper 阻止；核对来源与 SHA-256 后，可在系统设置的“隐私与安全性”中允许打开。TUN 需要系统管理员授权。

## Nowhere 使用

- 支持添加、编辑、导入和导出 `nowhere://` 节点，支持 TCP/TLS、QUIC 载体和 Morph。
- 订阅响应可返回 UTF-8 纯文本，每行一个分享链接；也支持将整个链接列表 Base64 编码。
- 通过独立 Nowhere Vector 进程提供本地 SOCKS，再与现有 Xray/sing-box 代理、路由和 TUN 流程衔接。
- 内核使用固定版本及 SHA-256 校验，更新先验证再替换，失败回滚；不下载任意未经固定信任的 Nowhere 新版本。

```text
nowhere://0123456789abcdef0123456789abcdef@example.com:443?net=tcp&morph=1#Example
```

示例密钥仅为格式演示。分享链接、订阅地址和配置备份包含凭据，请勿公开。

参阅 [Windows 快速开始](docs/nowhere-windows-quickstart.md)、[集成说明](docs/nowhere-integration.md)、[构建与发版](docs/nowhere-release.md)。

## 自动构建与发版

现仅保留 **Nowhere build and release** 一条流水线，构建 Windows x64 与 macOS arm64。master 源码变更 / PR 自动验证；手动运行也无需签名密钥即可获得 Windows ZIP、Apple Silicon DMG/ZIP，共 3 个安装包。推送 `nowhere-vX.Y.Z` 标签则自动构建两个平台并发布预发布版本；正式发布前需配置本分支自己的 GPG 密钥。详细步骤见 [发版文档](docs/nowhere-release.md)。

每个包附有 SHA-256 清单。标签发布另生成 `.asc` 分离签名及本分支公钥；**上游 GPG 公钥不能验证本分支发布包**。Apple 代码签名与 GPG 包签名是两套独立机制。

## 上游文档与致谢

通用功能和配置说明参阅 [上游 Wiki](https://github.com/2dust/v2rayN/wiki)。感谢 v2rayN、Nowhere、Xray、sing-box 及其贡献者。本分支不是上游官方发布，保留原项目许可及归属信息。
