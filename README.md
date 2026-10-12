# PocketMic · 随声

**把安卓手机变成 Windows 电脑的麦克风。** 戴上你喜欢的耳机，收音交给手机。

原生安卓采音 → 局域网加密连接 → Windows 音频输出 → 虚拟音频设备 → 会议 / 游戏 / OBS。

[下载 Windows 和 Android 首版](https://github.com/0xiaoyun0/pocketmic/releases) · [快速上手](docs/QUICKSTART.zh-CN.md) · [协议与架构](docs/ARCHITECTURE.md) · [测试说明](docs/TESTING.md)

## 首版能力

- **扫码配对**：Windows 显示二维码，安卓应用扫码保存电脑地址和证书指纹；以后点击连接即可。
- **局域网直连**：同一 Wi-Fi、手机热点或 USB 调试转发。不需要账号或音频服务器，传音不依赖互联网；首次驱动下载和更新需要联网。
- **加密传输**：TLS 1.2 / 1.3，扫码绑定 SHA-256 证书指纹，256 位随机配对密钥。电脑可撤销旧配对。
- **原生采音**：48 kHz、16 位、单声道 PCM，20 ms 一帧；设备支持时启用系统降噪和回声消除。
- **稳定性机制**：有界音频队列、旧帧丢弃、播放预缓冲、心跳检测、断线重连。
- **锁屏工作**：安卓前台麦克风服务，通知栏可静音和停止；静音会暂停 AudioRecord 采音。
- **桌面控制**：实时音量、网络往返时间、缓冲时间、增益、静音、设备选择、诊断导出。
- **自动准备驱动**：PC 自动检测缺失、从官网下载、校验完整包和数字签名、调用安装器并刷新设备。
- **自动更新**：两端自动检查 GitHub Release 并后台下载；Windows 空闲时自动替换，Android 由系统确认安装。参见[更新说明](docs/UPDATES.md)。
- **隐私**：不录制文件、不上传音频、不采集遥测；配对信息由 Windows DPAPI / Android Keystore 保护。

## 一次设置，以后直接连接

1. 从 [Releases](https://github.com/0xiaoyun0/pocketmic/releases) 下载 Windows ZIP 和 Android APK。Windows 包自带运行时，解压运行 `PocketMic.exe`。
2. Windows 首次运行会自动检测、下载、校验并安装 [VB-CABLE](https://vb-audio.com/Cable/) 虚拟音频驱动。确认 Windows 管理员授权；如需重启，按软件提示完成。软件会自动选择已就绪的虚拟设备。
3. 两台设备连接同一 Wi-Fi / 手机热点。安卓 PocketMic 扫描电脑二维码、保存配对，点击 **连接并开启麦克风**，允许麦克风权限。
4. PocketMic 的输出设备选 **CABLE Input**；会议、游戏或 OBS 的麦克风选 **CABLE Output**。耳机仍用作应用播放设备。

> **系统麦克风依赖虚拟驱动。** 单纯把声音播放到扬声器，不会生成 Windows 录音设备。未安装驱动时可用监听模式验证连接，但不能作为系统麦克风。首版没有宣称免驱即插即用。

## 适用范围

| 项目 | v0.1.0 |
|---|---|
| 电脑 | Windows 10 / 11 x64，WPF 原生应用 |
| 手机 | Android 8.0+（API 26+），原生 Java 应用 |
| 网络 | 私有 IPv4 局域网；TCP 47655 |
| 音频 | 48 kHz / 16-bit / mono，约 96 KB/s，另加 TLS 开销 |
| 目标缓冲 | 40 / 60 / 100 ms；不是端到端延迟承诺 |
| 并发 | 一台手机连接一台接收端 |
| 暂不支持 | iPhone、蓝牙音频链路、公网穿透、免驱虚拟麦克风、开机自动录音 |

这是可构建、可运行的 **预览版**。自动化覆盖 Windows TLS 接收、音频缓冲、Java 协议互通及安卓静态检查；尚未完成安卓真机 → VB-CABLE → 会议软件的完整实测，也未测量端到端延迟。请参考 [验证范围](docs/TESTING.md)，不要将界面的网络 RTT 当作录音到应用的实际延迟。

## 开发与构建

需要 .NET 10 SDK、JDK 17、Android SDK Platform 35 / Build Tools 34.0.0。Gradle 8.7 wrapper 已包含并固定分发包 SHA-256。

```powershell
# Windows：测试 + 自包含可执行文件 + ZIP
./scripts/build-windows.ps1

# Android：调试 APK、协议单元测试和 lint
cd android
./gradlew.bat :app:assembleDebug :app:testDebugUnitTest :app:lintDebug
cd ..

# 使用生产 Java 协议 / 证书验证代码对接实际 Windows TLS 接收端
./scripts/test-interop.ps1

# 本机签名发布 APK（Windows，设置 JAVA_HOME 为 JDK 17）
./scripts/build-android-release.ps1
```

安卓 SDK 路径通过 `ANDROID_HOME` 或未提交的 `android/local.properties` 设置。macOS/Linux 可用 `./gradlew` 构建安卓；Windows 接收端需 Windows 构建运行。

发布签名脚本首次生成独立 APK 签名密钥，保存到 `%LOCALAPPDATA%\PocketMicBuild`，密码用当前 Windows 用户的 DPAPI 保护。**请备份密钥和密码文件，保持后续 APK 的签名一致；恢复到其他用户 / 电脑前需自行安全迁移密码。** 仓库和 CI 不包含发布私钥；CI 的 APK 是调试签名，不可覆盖不同签名的 Release APK。

```text
desktop/PocketMic.Core       协议、配对、TLS 接收、音频缓冲
desktop/PocketMic.Windows    桌面界面、WASAPI、设备选择、配对身份
android/app                 安卓界面、扫码、前台录音、证书固定
tests                       .NET 测试和 Java ↔ Windows 互通测试
scripts                     构建、验证和可选防火墙配置
```

## 常见问题

**扫码后连不上？** 两端在同一网络；Windows 网络类型应为可信的“专用网络”；允许本地子网 TCP 47655。关闭路由器访客隔离，检查 VPN。需要时以管理员运行发行包的 `Allow-Firewall.ps1`。不要开放公网端口。

**连上了但会议里没声音？** 检查 PocketMic 的 `CABLE Input` 与会议应用的 `CABLE Output`。确认手机、桌面和会议软件均未静音。物理扬声器监听不会生成系统麦克风。

**锁屏后断开？** 安卓前台服务和唤醒锁用于保持连接，但厂商省电策略仍可能终止应用。给 PocketMic 允许后台运行；系统杀死服务后需要手动点连接，不会偷偷恢复采音。

**声音延迟或卡顿？** 优先同一 5 GHz Wi-Fi，拥挤网络使用“更稳定”缓冲。TCP 在丢包时仍可能停顿；需要有线链路可按[上手文档](docs/QUICKSTART.zh-CN.md)使用 USB 调试转发。

MIT 开源。依赖和独立驱动许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
