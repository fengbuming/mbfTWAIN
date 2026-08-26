# mbfTwain v1.1.0

这是 `v1.0.3` 的更新检查可靠性增强版本，为国内网络环境下的检查更新与安装包下载增加镜像兜底与完整性校验。

## 新增内容

- **更新检查增加镜像兜底（对齐 codex-usage-hud 方案）**：
  - 检查更新：官方 `api.github.com` 优先，官方请求失败后自动回退到 `gh-proxy.com` 镜像，两者任一成功即可拿到最新版本信息。
  - 安装包下载：官方 `github.com` 直链优先，失败后依次回退到 `ghproxy.net`、`gh-proxy.com` 两个镜像源，避免 `github.com` 或 `objects.githubusercontent.com` 不通导致下载失败。
  - 官方/镜像请求分别设置独立超时（官方 60s、镜像 90s），单个慢端点不会拖死整体流程。
- **安装包完整性校验**：
  - 解析 GitHub Release API 返回的 `digest`（`sha256:<hex>`）字段。
  - 下载完成后先校验文件大小，再校验 SHA-256，不匹配则丢弃文件并阻止启动安装，防止镜像劫持或传输损坏。
  - 官方未提供 digest 时降级为仅大小校验，保持向后兼容。

## 修复内容

- 修复检查更新在官方 `api.github.com` 失败时直接报"无法访问 GitHub"、没有其他路径可用的体验问题。
- 修复安装包下载在 `github.com/releases/download` 被阻断时直接失败、无退化路径的问题。

## 技术要点

- 更新检查框架保持"官方优先 + 镜像兜底"：能直连 GitHub 的用户不受影响，依然走最快最权威的官方端点。
- 镜像只做传输代理，不做信任源；版本判断与文件校验始终以 GitHub Release 元数据为准。
- 404（私有仓库）、401/403（未授权/超过限额）视为确定性错误，不进行镜像重试，避免无效请求。

## 安装包

下载安装资产：

- `mbfTwain-Setup-v1.1.0.exe`
- `mbfTwain-Setup-v1.1.0.exe.sha256`

安装器需要管理员权限，因为 TWAIN source 需要写入 Windows 的 TWAIN 目录。
如果仓库保持私有，UI 更新检查需要在启动前设置可读取 release 的
`MBF_TWAIN_GITHUB_TOKEN`；公开仓库不需要令牌。