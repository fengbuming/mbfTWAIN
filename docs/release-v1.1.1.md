# mbfTwain v1.1.1

这是 `v1.1.0` 的故障根治版本，解决换用新版扫描端后虚拟扫描仪长期卡在「正在送图 / 等待 TWAIN 程序取图」的问题。

## 修复内容

- **根治「正在送图」卡死**：旧会话遗留的 UI 进程（管理员权限）独占命名管道后，新会话的 DS 连接被系统拒绝（error=5）却被误判为「没有 UI」，导致反复拉起新 UI 且无人应答。本次从 DS 与 UI 两侧彻底修复。
- **DS 区分连接错误码**：`ERROR_ACCESS_DENIED(5)` / `ERROR_PIPE_BUSY(231)` 时不再拉起重复 UI，直接失败返回，避免 UI 进程堆叠。
- **UI 管道被占用时自醒退出**：不再静默挂死。
- **管道安全描述符放开 + Medium 完整性标签**：同用户跨 UAC 提权级别也能正常连接，消除「提权老 UI vs 普通权限新 DS」互斥。
- **会话结束自动退出 + 单实例互斥体**：`HIDE_SCAN_UI 0` 后 2 秒自动退出（新会话可取消），并新增具名互斥体杜绝多 UI 并存。
- **空闲兜底超时**：默认 10 分钟无客户端连接自动退出（环境变量 `MBF_TWAIN_UI_IDLE_TIMEOUT_MINUTES` 可调，整数分钟）。

## 技术要点

- 命名管道 `\\.\pipe\mbfTwain.VirtualScanner.v1` 采用 `Everyone / SYSTEM / Administrators` 读写权限 + Medium 强制完整性标签。
- UI 生命周期三路径回收：会话结束延迟退出（可被新会话取消）、空闲超时退出、管道重名自醒退出，杜绝驻留进程占管道的复发。
- 全量冒烟（x64/Win32 × 基础 / 延迟就绪 / 未选图关闭 / XferCount）全部通过；定向验证覆盖跨权限连接、重复实例自退、会话结束自退、空闲超时自退（`tools/verify-ui-fix.ps1`）。

## 安装包

下载安装资产：

- `mbfTwain-Setup-v1.1.1.exe`
- `mbfTwain-Setup-v1.1.1.exe.sha256`

安装器需要管理员权限，因为 TWAIN source 需要写入 Windows 的 TWAIN 目录。如果仓库保持私有，UI 更新检查需要在启动前设置可读取 release 的 `MBF_TWAIN_GITHUB_TOKEN`；公开仓库不需要令牌。
