# 自动更新与发布

Windows 和 Android 启动后检查本仓库公开 GitHub Releases，应用保持运行时每六小时检查一次。两端提供手动检查入口。使用发布标签 `vMAJOR.MINOR.PATCH`，并读取同一 Release 的 `update-manifest.json`；仅选择比当前版本新的版本，不会降级。预览版也跟随公开的同格式发布标签更新。

下载的文件受固定仓库 HTTPS 地址、清单中的精确文件名、长度与 SHA-256 校验约束。清单本身由 GitHub HTTPS 提供，Windows 更新信任仓库发布权限；目前没有独立离线签名清单。安卓额外验证包名、递增 versionCode 和与已安装 APK 完全相同的签名证书。调试签名 APK 不能接收不同签名的正式版更新，使用 Release APK 才能保持正常更新链路。

- **Windows**：后台下载并校验，空闲十秒后自动替换自包含 EXE 并重新启动。手机连接或驱动安装过程中不会更新。更新辅助进程等待原进程退出；保留 `PocketMic.exe.previous` 备份，文件替换失败时尝试恢复旧版。请放在当前用户可写目录；Program Files 等受保护目录需要自行授权迁移。配对与设置在 `%LOCALAPPDATA%\PocketMic`，不随 EXE 替换。
- **Android**：后台下载，界面显示“点击安装更新”；正在采音时要求先断开。首次需按系统提示允许 PocketMic 安装应用，之后由系统安装器确认。普通 Android 应用不能静默覆盖自身，未做权限绕过。配对与设置随正常覆盖安装保留。应用关闭后不额外启动后台更新服务。
- **离线**：检查失败不会影响局域网音频；下次检查或点击重试即可。

## 发布新版本

1. 更新根目录 `VERSION`（如 `0.1.1`），构建脚本、.NET 与 Android 均从这里读取版本。Android versionCode 计算为 `major × 1000000 + minor × 1000 + patch`，各分段须小于 1000，并始终递增。
2. 用 `scripts/build-windows.ps1` 与 `scripts/build-android-release.ps1` 完成校验和签名构建。始终使用原 APK 签名密钥。
3. 运行 `scripts/make-update-manifest.ps1`，生成版本匹配的清单和 SHA256SUMS。
4. 创建同名 Git 标签及 GitHub Release，先以 draft 状态上传 Windows ZIP、Android APK、清单与校验文件，全部完成后发布。不要发布缺少清单的更新。
5. 确认两端旧版本能检查到新版本，并在独立测试机验证升级前后的配对与音频设置。

## 驱动自动准备

Windows 初次缺少虚拟麦克风时自动从 VB-Audio 官网下载原版 VB-CABLE Pack45，验证固定 SHA-256（完整包和安装 EXE）及 Windows Authenticode 后启动安装器 `-i -h`。需要 Windows 管理员授权，系统要求重启时仅提示，不强制重启。安装结束轮询实际音频端点，出现可用设备后自动选择。取消授权或失败后可点击重试；已存在但禁用/待重启的设备不重复安装。

VB-CABLE 可明确识别为独立 VB-Audio 产品，PC 界面保留厂商来源、Donationware 说明和支持作者入口；参见[厂商分发政策](https://vb-audio.com/Services/licensing.htm)。本项目不向 GitHub 上传驱动二进制。企业批量部署应遵循厂商对应许可。

未来厂商替换驱动包时哈希不一致会停止安装，需要发布 PocketMic 新版本更新固定哈希；不会直接执行未经校验的新文件。自动下载与 Authenticode 流程有本机测试；驱动提权安装、重启后可用性及两端覆盖升级仍需实机验收。
