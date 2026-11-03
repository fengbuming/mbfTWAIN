# 测试机手动覆盖：旧版 TWAIN DSM 等待取图修复

本次修复让 mbfTwain 在旧版 `twain_32.dll` 未提供 `DAT_ENTRYPOINT` 时，从扫描端已加载的管理器中获取 `DSM_Entry`，发送取图就绪和取消通知。

## 覆盖步骤

1. 将修复包 `mbfTwain-legacy-dsm-fix.zip` 复制到测试机并解压。
2. 完全退出扫描客户端，关闭所有 mbfTwain 窗口。在任务管理器中确认 `MSZS.ScanClient.ManageUpgrade.exe` 和 `mbfTwain.VirtualScannerConfig.exe` 已退出。只关闭选图窗口不够，驱动仍可能被扫描客户端加载。
3. 将原来下面两个目录中的 `mbfVirtualTwainDS.ds` 和 `mbfTwain.VirtualScannerConfig.*` 文件备份到其他目录。
4. 将修复包 `Win32` 文件夹内的全部五个文件复制到 `C:\Windows\twain_32`，选择替换同名文件，并同意管理员权限提示。
5. 将修复包 `x64` 文件夹内的全部五个文件复制到 `C:\Windows\twain_64`，选择替换同名文件。
6. 重新启动扫描客户端，选择 mbf 虚拟扫描仪，再开始扫描。

每个文件夹均包含：

```text
mbfVirtualTwainDS.ds
mbfTwain.VirtualScannerConfig.exe
mbfTwain.VirtualScannerConfig.dll
mbfTwain.VirtualScannerConfig.deps.json
mbfTwain.VirtualScannerConfig.runtimeconfig.json
```

Win32 对应 32 位扫描客户端，x64 对应 64 位扫描客户端。测试机当前扫描端为 32 位，实际加载的是 `C:\Windows\twain_32\mbfVirtualTwainDS.ds`。两个架构的 `.ds` 不能混用。

如果提示文件正在使用，请先确认上述进程已退出，再复制。无需替换 `C:\Windows\twain_32.dll`，无需运行 regsvr32。

测试机之前已确认机器环境变量 `MBF_TWAIN_FORCE_UI=1`；本次覆盖无需重新设置。修复包内附 `SHA256SUMS.txt`，用于核对复制文件。

## 验收

1. 选择一张图片并点击“开始扫描”，确认扫描端实际收到图片，等待取图提示结束。
2. 再扫描一次，确认连续会话正常。
3. 选择多张图片，确认接收页数和顺序符合扫描端设置。
4. 新开扫描会话后不选图片直接关闭，确认扫描端可退出扫描并能再次发起。

如果仍卡住，请保留现场，提供当前用户 `%TEMP%\mbfTwain-diagnostics.log`。修复后旧版 DSM 路径应出现 `Resolved legacy DSM_Entry`，随后出现 `NotifyTransferReady DAT_NULL/MSG_XFERREADY rc=0`。

需要回退时，退出扫描端和 mbfTwain 后，将备份文件按原目录覆盖回去。
