# 检查更新镜像兜底改造说明

日期：2026-08-26
参考项目：`E:\Project\codex-usage-hud`（`src/codex_usage_hud/updater.py`）

## 改动文件

| 文件 | 改动 |
|---|---|
| `src/VirtualScannerConfig/Updates/GitHubUpdateService.cs` | 重写核心逻辑，增加 API 镜像回退、下载镜像回退、SHA-256 校验 |
| `src/VirtualScannerConfig/Updates/ReleaseUpdateInfo.cs` | 新增 `InstallerSha256` 字段 |

## 设计原则（对齐 codex-usage-hud）

1. **官方优先**：官方 `api.github.com` / `github.com` 直链永远是第一候选。能直连的用户享受最低延迟和最权威响应，镜像只在官方失败后介入。
2. **镜像做传输层，不做信任层**：版本信息以官方/镜像 API 返回为准（同一仓库的代理），安装包下载后按 GitHub Release 的 `digest`（`sha256:<hex>`）做完整性校验，镜像被投毒也无法通过校验。
3. **确定性错误不重试**：404（私有仓库）、401/403（未授权/限额）抛 `UpdateSourceUnavailableException`，不浪费时间去镜像重试。网络类错误才走回退链。

## 检查更新（元数据）链路

```
api.github.com/repos/.../releases/latest        （官方，超时 20s）
  └─ 失败 ─> https://gh-proxy.com/{官方URL}      （镜像，超时 20s）
```

## 安装包下载链路

```
官方 browser_download_url（github.com 直链，超时 60s）
  └─ 失败 ─> https://ghproxy.net/{官方URL}       （镜像1，超时 90s）
  └─ 失败 ─> https://gh-proxy.com/{官方URL}       （镜像2，超时 90s）
```

每个来源独立超时（各自新建 HttpClient），避免一个慢端点拖死整体。

## SHA-256 校验

- GitHub Release API 的 asset 通常带 `digest: "sha256:<hex>"` 字段（实测 v1.0.3 安装包确实带）。
- 下载完成后：先校验文件大小，再校验 SHA-256；不匹配则删除部分文件并抛错，禁止启动安装。
- 官方 API 未提供 digest 时降级为仅大小校验，不阻断下载（保持向后兼容）。

## 验证结果

- `dotnet build`：0 警告 0 错误（多次确认）。
- 完整构建（Win32/x64 DS + UI + smoke）：全部通过（基础、UI delayed-ready、close-without-selection、xfercount 各平台均绿）。
- 网络实测（本机）：
  - 官方 API：HTTP 200，~2.1s
  - gh-proxy.com 镜像 API：HTTP 200，~7.5s
  - 官方下载、ghproxy.net、gh-proxy.com 三条下载链均能返回正确数据（206 + MZ 文件头）

## 待办

- [ ] 本地安装到 `C:\Windows\twain_32/64`（需管理员）：当前会话无管理员权限且沙箱拦截 UAC 提权，需手动运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-LocalTwain.ps1
```

（也可以在管理员 PowerShell 里直接 `.\Install-LocalTwain.ps1`）

## 备注

- 镜像站有生命周期，本次选用的 `gh-proxy.com` / `ghproxy.net` 是 2026-08 实测可用的主流站点；若失效只需修改 `GitHubUpdateService.cs` 顶部的 `MirrorApiTemplate` / `MirrorDownloadTemplate` 常量。