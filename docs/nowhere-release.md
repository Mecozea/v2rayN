# Nowhere 双平台构建与发版

唯一入口为 `.github/workflows/nowhere-release.yml`（Actions 名称：**Nowhere build and release**）。按当前需求仅构建 Windows x64 WPF 与 macOS arm64 Avalonia。原先 14 个全平台、WebAPI 独立打包、重复测试和 WinGet 工作流已移除，可从 Git 历史恢复；WebAPI 的共享回归测试并入双平台任务，不再单独分发 WebAPI 包。

## 触发方式

| 操作 | 行为 | 是否创建 Release |
| --- | --- | --- |
| 向 master 推送相关源码 / 流水线变更 | 两个平台构建、原生测试、打包并保存 Artifacts | 否 |
| 向 master 提交相关 PR | 同上，构建任务不使用发布密钥 | 否 |
| Run workflow | 同上；版本留空读取 `v2rayN/Directory.Build.props`，也可填写 X.Y.Z | 否，即使手动选择了 tag |
| 推送 `nowhere-vX.Y.Z` 标签 | 两个平台验证通过后检查包完整性、GPG 签名并上传至当前 fork 的 prerelease | 是 |

文档等不影响构建的修改不触发自动构建；标签推送不受路径过滤限制。普通分支 / PR 的新提交会取消相同分支 / PR 的旧运行；标签构建不取消。两个构建任务均设置 45 分钟上限，SDK 下载和 NuGet 依赖由 Actions 管理并缓存。

构建版本来源依次为：推送的标签版本、手动填写版本、Directory.Build.props。Windows/macOS 使用同一个值发布 GUI 与更新器。当前源码默认版本为 7.25.7。

## 产物与数量

| 构建平台 | GitHub runner | 安装 / 更新包 |
| --- | --- | --- |
| Windows x64 | windows-latest | `v2rayN-windows-64.zip` |
| macOS arm64 | macos-15 | `v2rayN-macos-arm64.dmg`、`v2rayN-macos-arm64.zip` |

共 **3 个二进制包**。另生成两份 `SHA256SUMS-*.txt`，对应两个 `nowhere-*` Artifacts。TRX / 回环诊断另存为 `diagnostics-*`，保留 14 天。构建 Artifacts 只有全部相应测试通过才上传；失败时仍尽量保存诊断。

标签发布上传 3 个包 + 2 份校验清单 + 对应 5 个 `.asc` 签名 + 1 个公钥，共 **11 个上传资产**；加 GitHub 自动提供的 source ZIP/tar.gz，页面共约 **13 项**。这不是 13 个独立安装包。

上游 7.25.6 的截图显示 43 项：API 实际列出 20 个包、20 个 `.sig`、1 个公钥；页面另加 2 个自动源码包。20 个包覆盖 Windows 三种架构的 WPF/Avalonia、Linux DEB/RPM/ZIP 和 macOS 两种架构。当前只需要两个平台，无需复制该发布矩阵。

## 需要配置的 Secrets / 环境

| 配置 | 何时需要 | 内容 |
| --- | --- | --- |
| `GPG_PRIVATE_KEY` Repository secret | 标签公开发布时必需；普通构建不需要 | 本 fork 发布密钥的完整 ASCII-armored 私钥 |
| `GPG_PASSPHRASE` Repository secret | 仅私钥带口令时 | 私钥口令；无口令密钥可不设置 |
| `GITHUB_TOKEN` | GitHub 自动提供 | 无需手动创建 PAT；发布 job 声明 `contents: write` |
| Apple 开发者证书 / 公证账号 | 当前不需要 | 当前为 ad-hoc 签名，不执行 Developer ID 签名或 notarization |

在仓库 Settings → Secrets and variables → Actions 中配置前两项，并启用 GitHub Actions。组织策略须允许工作流声明的权限和使用官方 Actions；没有额外必填 Repository Variables。SDK / Python / 内核版本或下载路径不需要手动填环境变量。

私钥仅在独立发布 job 导入临时 GNUPGHOME；口令通过 stdin 传给 GPG，运行结束清理密钥目录。缺少密钥时发布明确失败，构建 Artifacts 仍保留。此前沿用的独立发布工作流只支持无交互密钥，已不再使用。

## 操作步骤

测试包：将修改推送到 fork，然后 Actions → Nowhere build and release → Run workflow，下载 `nowhere-windows-x64` 或 `nowhere-macos-arm64`。

公开预发布：确认修改已提交、目标 tag 尚未使用且发布密钥已配置，然后执行：

```sh
git tag nowhere-v7.25.7
git push origin nowhere-v7.25.7
```

标签应指向需要发布的提交。发布目标使用 `github.repository`，即本 fork `Mecozea/v2rayN`。未调用上游发布或 WinGet；客户端 GUI 更新源同样指向本 fork。流水线创建 prerelease，客户端需勾选“检查 Pre-Release”才能发现预发布；完成平台验收后再在 GitHub 将其转为稳定版。

验证下载文件：

```sh
gpg --import v2rayN-public-key.asc
gpg --fingerprint
gpg --verify v2rayN-macos-arm64.dmg.asc v2rayN-macos-arm64.dmg
shasum -a 256 -c SHA256SUMS-macos-arm64.txt
```

公钥指纹应通过独立可信渠道核对。GUI 更新器目前不会自动验证此 GPG 签名；Nowhere 内核安装另外使用源码固定版本与双 SHA-256。

## 本地复现与验证边界

Windows x64 需 .NET 10 SDK：

```powershell
./scripts/Publish-NowhereWindows.ps1 -OutputDirectory ./dist/windows-x64 -Version 7.25.7
```

Apple Silicon Mac 需 .NET 10 SDK、Python 3.13+、Xcode Command Line Tools：

```sh
bash scripts/package-nowhere-macos.sh 7.25.7
```

输出目录应为空 / 不存在。可用 `NOWHERE_MACOS_CACHE=/absolute/cache` 复用缓存，仍逐项核对固定 SHA-256。macOS 程序与原生库放在 Contents/MacOS，内核 / 规则 / 标记放在 Contents/Resources；完整 App ZIP 用于更新器整体替换。官方 Nowhere 可执行文件保持原始字节与签名。

2026-10-30 核对远端：fork 旧提交 `d7ff14c7` 最近 6 次普通构建 / 测试均成功；未证明原发布链路、GPG secret 或本次精简后的工作流已在 Actions 跑通。本轮只读查询、修改本地工作流并运行 actionlint 和 6 种版本解析检查，未推送、触发 Actions 或发布 Release。本地 7.25.7 macOS 包已通过原生测试、签名和镜像校验，详见 `nowhere-macos-validation-2026-10-30.md`；GUI / 公网 / TUN 验收仍按独立清单执行。

旧全平台发布还存在 master 分支硬编码、dispatch 不等待结果、旧式 Mac ZIP 与完整 bundle 更新不兼容等问题；RISC-V runner 的访问也不能假定从上游继承。精简后不再执行这些路径，不要求配置 RISC-V / LoongArch 自托管环境。
