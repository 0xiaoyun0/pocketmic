using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace PocketMic.Windows;

internal enum DriverState { Missing, Ready, DisabledOrRestartRequired }
internal static class DriverInstaller
{
    public const string DownloadUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    public const string PackageHash = "B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
    public const string SetupHash = "734C35DFA6D98F48782A451633CEB471166EC70D60482FD89A1123D0EE3C4F41";
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketMic", "driver");
    public static bool Attempted => File.Exists(Path.Combine(Folder, "attempted"));
    public static DriverState Detect()
    {
        using var enumerator = new MMDeviceEnumerator(); bool present = false, render = false, capture = false;
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        using (endpoint)
        {
            if (!endpoint.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) && !endpoint.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase)) continue;
            present = true;
            if (endpoint.State == DeviceState.Active) { render |= endpoint.DataFlow == DataFlow.Render; capture |= endpoint.DataFlow == DataFlow.Capture; }
        }
        if (render && capture) return DriverState.Ready;
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\VBAudioVACMME");
        return present || service != null ? DriverState.DisabledOrRestartRequired : DriverState.Missing;
    }
    public static async Task<string> PrepareAsync(IProgress<string> progress, CancellationToken cancel)
    {
        var staging = Path.Combine(Folder, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        var zip = Path.Combine(staging, "driver.zip");
        progress.Report("正在从 VB-Audio 官网下载虚拟麦克风驱动…");
        // A pinned complete archive also protects the INF/CAT/SYS files used by the elevated setup.
        using var client = Downloads.Client();
        using var response = await client.GetAsync(DownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var target = File.Create(zip))
        {
            var bytes = new byte[32768]; long total = 0; int n;
            while ((n = await source.ReadAsync(bytes, cancel)) > 0) { total += n; if (total > 5_000_000) throw new InvalidDataException("驱动下载超出大小限制。"); await target.WriteAsync(bytes.AsMemory(0, n), cancel); }
        }
        using (var input = File.OpenRead(zip))
            if (Convert.ToHexString(await SHA256.HashDataAsync(input, cancel)) != PackageHash) throw new InvalidDataException("驱动包校验失败，请更新 PocketMic 后重试。");
        var extracted = Path.Combine(staging, "setup"); ZipFile.ExtractToDirectory(zip, extracted);
        var setup = Path.Combine(extracted, "VBCABLE_Setup_x64.exe");
        using (var input = File.OpenRead(setup))
            if (Convert.ToHexString(await SHA256.HashDataAsync(input, cancel)) != SetupHash) throw new InvalidDataException("驱动安装程序校验失败。");
        progress.Report("正在验证 VB-Audio 官方数字签名…");
        if (!await Task.Run(() => Authenticode.Verify(setup), cancel)) throw new InvalidDataException("Windows 无法验证驱动安装程序的数字签名，已停止安装。");
        return setup;
    }
    public static async Task<string> InstallAsync(IProgress<string> progress, CancellationToken cancel)
    {
        var state = Detect();
        if (state == DriverState.Ready) return "虚拟麦克风已就绪。";
        if (state == DriverState.DisabledOrRestartRequired) return "驱动已存在，但端点未就绪。请先重启电脑或在声音设置中启用 CABLE 设备，避免重复安装。";
        Directory.CreateDirectory(Folder); File.WriteAllText(Path.Combine(Folder, "attempted"), DateTimeOffset.UtcNow.ToString("O"));
        var setup = await PrepareAsync(progress, cancel);
        progress.Report("请确认 Windows 管理员授权，随后自动安装驱动…");
        using var process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", Arguments = "-i -h", WorkingDirectory = Path.GetDirectoryName(setup)! }) ?? throw new IOException("无法启动驱动安装程序。");
        // Never kill a running driver installer or force a reboot.
        await process.WaitForExitAsync();
        for (int i = 0; i < 15; i++)
        {
            if (Detect() == DriverState.Ready) return "虚拟麦克风已安装并自动连接。厂商建议首次安装后重启电脑。";
            await Task.Delay(1000, cancel);
        }
        return $"驱动安装程序已结束（退出码 {process.ExitCode}）。请重启电脑后打开 PocketMic，软件将自动识别设备。";
    }
}
