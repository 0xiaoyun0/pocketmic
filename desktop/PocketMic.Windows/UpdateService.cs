using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PocketMic.Core;

namespace PocketMic.Windows;

internal sealed record PendingUpdate(string Exe, string Hash, string Version);
internal sealed record UpdatePlan(int ParentId, long ParentStartTicks, string Target, string TargetHash, string Source, string Hash);
internal static class UpdateService
{
    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketMic", "updates");
    public static async Task<PendingUpdate?> DownloadNewVersionAsync(IProgress<string> progress, CancellationToken cancel)
    {
        using var client = Downloads.Client();
        using var response = await client.GetAsync($"https://api.github.com/repos/{ReleaseManifest.Repository}/releases?per_page=20", cancel);
        response.EnsureSuccessStatusCode();
        using var releases = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var newest = releases.RootElement.EnumerateArray().Where(r => !r.GetProperty("draft").GetBoolean())
            .Select(r => r.GetProperty("tag_name").GetString()).Where(t => t != null)
            .Select(t => { try { return ReleaseManifest.ParseVersion(t!); } catch (FormatException) { return null; } })
            .Where(v => v != null && v > ReleaseManifest.ParseVersion(CurrentVersion)).OrderDescending().FirstOrDefault();
        if (newest == null) return null;
        var version = newest.ToString(3);
        var manifestText = await client.GetStringAsync(ReleaseManifest.AssetUri(version, "update-manifest.json"), cancel);
        if (manifestText.Length > 16384) throw new InvalidDataException("更新清单过大。");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestText, Protocol.Json) ?? throw new InvalidDataException("更新清单无效。");
        manifest.Validate("v" + version);
        var staging = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
        var zip = Path.Combine(staging, manifest.Windows.Name);
        progress.Report($"发现 v{version}，正在后台下载…");
        await Downloads.FetchAsync(ReleaseManifest.AssetUri(version, manifest.Windows.Name), zip, manifest.Windows.Sha256, manifest.Windows.Size, null, cancel);
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.GetEntry("PocketMic.exe") ?? throw new InvalidDataException("更新包缺少 PocketMic.exe。");
        if (entry.Length is < 1000000 or > 300000000) throw new InvalidDataException("更新程序大小异常。");
        var exe = Path.Combine(staging, "PocketMic.exe"); entry.ExtractToFile(exe, true);
        using var file = File.OpenRead(exe);
        return new PendingUpdate(exe, Convert.ToHexString(await SHA256.HashDataAsync(file, cancel)), version);
    }

    public static void LaunchReplacement(PendingUpdate update)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法定位当前程序。");
        if (!string.Equals(Path.GetFileName(executable), "PocketMic.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("自动替换仅支持发布的 PocketMic.exe；开发模式请手动构建。");
        var stage = Path.GetDirectoryName(update.Exe)!;
        var helper = Path.Combine(stage, "PocketMic.UpdateHelper.exe");
        File.Copy(executable, helper, true);
        var planPath = Path.Combine(stage, "plan.json");
        using var running = Process.GetCurrentProcess();
        using var current = File.OpenRead(executable);
        var targetHash = Convert.ToHexString(SHA256.HashData(current));
        File.WriteAllText(planPath, JsonSerializer.Serialize(new UpdatePlan(Environment.ProcessId, running.StartTime.ToUniversalTime().Ticks, executable, targetHash, update.Exe, update.Hash)));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(planPath);
        Process.Start(start)?.Dispose();
    }

    public static async Task ApplyAsync(string planPath, Action<string>? launch = null)
    {
        launch ??= path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        var full = Path.GetFullPath(planPath);
        if (!full.StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid update staging path.");
        var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(full)) ?? throw new InvalidDataException("Invalid update plan.");
        if (Path.GetDirectoryName(Path.GetFullPath(plan.Source)) != Path.GetDirectoryName(full)
            || !string.Equals(Path.GetFileName(plan.Target), "PocketMic.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid update paths.");
        await ReleaseManifest.VerifyFileAsync(plan.Source, plan.Hash, new FileInfo(plan.Source).Length);
        await ReleaseManifest.VerifyFileAsync(plan.Target, plan.TargetHash, new FileInfo(plan.Target).Length);
        Process? parent = null;
        try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { /* Parent may have exited before helper startup. */ }
        if (parent != null)
        using (parent)
        {
            if (!parent.HasExited)
            {
                if (!string.Equals(parent.MainModule?.FileName, plan.Target, StringComparison.OrdinalIgnoreCase) || parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks) throw new InvalidDataException("Update parent does not match the target.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await parent.WaitForExitAsync(timeout.Token);
            }
        }
        string backup = plan.Target + ".previous", next = plan.Target + ".next";
        bool moved = false;
        try
        {
            File.Copy(plan.Source, next, true);
            File.Move(plan.Target, backup, true); moved = true;
            File.Move(next, plan.Target, true);
            launch(plan.Target);
        }
        catch
        {
            if (moved) File.Copy(backup, plan.Target, true);
            launch(plan.Target);
            throw;
        }
    }
}
