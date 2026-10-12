using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PocketMic.Core;
using QRCoder;

namespace PocketMic.Windows;

public partial class MainWindow : Window
{
    private readonly IdentityStore identity;
    private readonly Receiver receiver;
    private readonly AudioOutput audio;
    private readonly DispatcherTimer meter = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string pairingLink = "";
    private bool closing, closed;
    private bool driverBusy, updateBusy, replacing;
    private PendingUpdate? pendingUpdate;
    private DateTimeOffset pendingSince;
    private readonly CancellationTokenSource tasks = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private Preferences settings = new();
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketMic", "settings.json");
    private sealed record Preferences(string? OutputId = null, int Gain = 100, int Buffer = 1);

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"WINDOWS   /   v{UpdateService.CurrentVersion}";
        identity = new IdentityStore();
        receiver = new Receiver(identity.Certificate, () => identity.Token);
        audio = new AudioOutput(receiver.Buffer);
        try { if (File.Exists(SettingsPath)) settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) ?? new(); } catch (Exception ex) when (ex is IOException or JsonException) { }
        receiver.ConnectionChanged += _ => Dispatcher.BeginInvoke(UpdateConnection);
        receiver.Error += message => Dispatcher.BeginInvoke(() => Notice.Text = message);
        audio.Failed += message => Dispatcher.BeginInvoke(() => { Notice.Text = message; OutputHint.Text = "输出已停止，请重新选择或刷新设备。"; });
        receiver.Start();
        LoadDevices();
        GainSlider.Value = settings.Gain;
        BufferSelect.SelectedIndex = Math.Clamp(settings.Buffer, 0, 2);
        ApplyAudioSettings();
        meter.Tick += (_, _) => UpdateMeter();
        meter.Start();
        Loaded += async (_, _) =>
        {
            _ = CheckUpdatesAsync(false);
            if (!AudioOutput.Devices().Any(d => d.IsVirtual) && !DriverInstaller.Attempted) await InstallDriverAsync();
        };
        updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(false);
        updateTimer.Start();
    }

    private void LoadDevices()
    {
        var address = AddressSelect.SelectedItem as string;
        var addresses = IdentityStore.Addresses();
        if (addresses.Count == 0) { addresses.Add("127.0.0.1"); Notice.Text = "未发现局域网地址。请连接 Wi-Fi / 手机热点后刷新；127.0.0.1 仅用于 USB 调试。"; }
        AddressSelect.ItemsSource = addresses;
        AddressSelect.SelectedItem = addresses.Contains(address ?? "") ? address : addresses[0];
        var prior = (OutputSelect.SelectedItem as OutputDevice)?.Id ?? settings.OutputId;
        var devices = AudioOutput.Devices();
        OutputSelect.ItemsSource = devices;
        OutputSelect.SelectedItem = devices.FirstOrDefault(d => d.Id == prior) ?? devices.FirstOrDefault(d => d.IsVirtual);
        if (OutputSelect.SelectedItem == null) { audio.Stop(); DriverButton.Visibility = Visibility.Visible; OutputHint.Text = "虚拟麦克风未就绪，软件将自动检测并安装 VB-CABLE。"; }
        UpdatePairing();
    }

    private void UpdatePairing()
    {
        if (identity == null || receiver == null || AddressSelect.SelectedItem is not string address) return;
        pairingLink = new Pairing(address, receiver.Port, identity.Token, identity.Fingerprint, Environment.MachineName).ToUri();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(pairingLink, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        using var stream = new MemoryStream(qr.GetGraphic(8));
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        PairQr.Source = bitmap;
    }

    private void UpdateConnection()
    {
        ConnectionBadge.Text = receiver.Connected ? "●  手机已连接" : "●  等待手机";
        DeviceLabel.Text = receiver.DeviceName ?? "等待第一声「你好」";
        StreamLabel.Text = receiver.Connected ? "48 kHz / 单声道 · 局域网加密传输" : "手机连接后，这里会显示实时输入";
        MuteButton.IsEnabled = receiver.Connected;
    }

    private void UpdateMeter()
    {
        var peak = receiver.Connected ? receiver.Buffer.Peak : 0;
        Waveform.Push(peak);
        RttLabel.Text = receiver.Connected ? $"{receiver.RoundTripMs} ms" : "— ms";
        BufferLabel.Text = receiver.Connected ? $"{receiver.Buffer.BufferedMs:0} ms" : "— ms";
        PeakLabel.Text = peak > .00003 ? $"{20 * Math.Log10(peak):0} dB" : "— dB";
        if (receiver.Connected && !audio.Running) StreamLabel.Text = "手机已连接 · 请先选择可用的输出设备";
        if (pendingUpdate != null && (receiver.Connected || driverBusy)) pendingSince = DateTimeOffset.UtcNow;
        if (pendingUpdate != null && !replacing && !receiver.Connected && !driverBusy && DateTimeOffset.UtcNow - pendingSince > TimeSpan.FromSeconds(10))
        {
            replacing = true;
            try { UpdateService.LaunchReplacement(pendingUpdate); Close(); }
            catch (Exception ex) { pendingUpdate = null; replacing = false; UpdateStatus.Text = $"更新未应用：{ex.Message}"; }
        }
    }

    private void AddressChanged(object sender, SelectionChangedEventArgs e) => UpdatePairing();
    private void CopyPairing(object sender, RoutedEventArgs e)
    {
        if (pairingLink.Length == 0) return;
        try { Clipboard.SetText(pairingLink); Notice.Text = "配对链接已复制，可在手机中粘贴连接。链接包含配对凭据，请仅用于自己的手机。"; }
        catch (System.Runtime.InteropServices.COMException) { Notice.Text = "剪贴板正忙，请重试。"; }
    }
    private void RotatePairing(object sender, RoutedEventArgs e)
    {
        identity.Rotate(); receiver.Disconnect(); UpdatePairing(); Notice.Text = "旧配对已失效，请在手机上重新扫描二维码。";
    }
    private void RefreshDevices(object sender, RoutedEventArgs e) { try { LoadDevices(); } catch (Exception ex) { Notice.Text = ex.Message; } }
    private void OutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (audio == null || OutputSelect.SelectedItem is not OutputDevice selected) return;
        try
        {
            audio.Start(selected.Id);
            OutputHint.Text = selected.IsVirtual ? "在会议 / 游戏的麦克风设置中，选择对应的 CABLE Output 或 VoiceMeeter Output。" : "当前为耳机 / 扬声器监听模式。要作为系统麦克风，请选择 CABLE Input。";
            DriverButton.Visibility = selected.IsVirtual ? Visibility.Collapsed : Visibility.Visible;
            settings = settings with { OutputId = selected.Id }; SaveSettings(); UpdateConnection();
        }
        catch (Exception ex) { OutputHint.Text = $"无法打开设备：{ex.Message}"; }
    }
    private void ToggleMute(object sender, RoutedEventArgs e)
    {
        audio.Muted = !audio.Muted;
        MuteButton.Content = audio.Muted ? "恢复输出" : "暂停输出";
        Notice.Text = audio.Muted ? "电脑输出已静音。手机仍在采音，可在手机上暂停麦克风。" : "电脑音频输出已恢复。";
    }
    private void GainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainLabel != null) GainLabel.Text = $"{e.NewValue:0}%";
        if (audio == null) return;
        audio.Gain = (float)e.NewValue / 100;
        settings = settings with { Gain = (int)e.NewValue }; SaveSettings();
    }
    private void BufferChanged(object sender, SelectionChangedEventArgs e)
    {
        if (receiver == null) return;
        ApplyAudioSettings(); settings = settings with { Buffer = BufferSelect.SelectedIndex }; SaveSettings();
    }
    private void ApplyAudioSettings()
    {
        audio.Gain = (float)GainSlider.Value / 100;
        receiver.Buffer.TargetMs = BufferSelect.SelectedIndex switch { 0 => 40, 2 => 100, _ => 60 };
    }
    private void SaveSettings() { try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings)); } catch (IOException) { } }
    private async void CheckUpdateClicked(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (updateBusy || pendingUpdate != null || closing) return;
        updateBusy = true;
        try
        {
            if (manual) UpdateStatus.Text = "正在检查 GitHub 更新…";
            var progress = new Progress<string>(text => UpdateStatus.Text = text);
            pendingUpdate = await UpdateService.DownloadNewVersionAsync(progress, tasks.Token);
            if (pendingUpdate != null) { pendingSince = DateTimeOffset.UtcNow; UpdateStatus.Text = $"v{pendingUpdate.Version} 已就绪，将在空闲时自动更新。"; }
            else UpdateStatus.Text = $"v{UpdateService.CurrentVersion} · 当前为最新版本";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { UpdateStatus.Text = manual ? "暂时无法检查更新，请检查网络后重试。" : "自动更新待网络恢复后重试"; }
        finally { updateBusy = false; }
    }
    private async void InstallDriverClicked(object sender, RoutedEventArgs e) => await InstallDriverAsync();
    private async Task InstallDriverAsync()
    {
        if (driverBusy || closing) return;
        driverBusy = true; DriverButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(text => { OutputHint.Text = text; Notice.Text = text; });
            Notice.Text = "首次准备麦克风：将安装 VB-Audio 的 VB-CABLE（Donationware）。可点击「支持作者」了解许可并捐赠。";
            var result = await DriverInstaller.InstallAsync(progress, tasks.Token);
            LoadDevices();
            if (DriverInstaller.Detect() == DriverState.Ready) OutputSelect.SelectedItem = (OutputSelect.ItemsSource as List<OutputDevice>)?.FirstOrDefault(d => d.IsVirtual);
            Notice.Text = result;
            if (DriverInstaller.Detect() != DriverState.Ready) OutputHint.Text = result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { OutputHint.Text = "管理员授权已取消，可点击「自动安装虚拟麦克风」重试。"; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OutputHint.Text = $"自动安装未完成：{ex.Message}"; }
        finally { driverBusy = false; DriverButton.IsEnabled = true; }
    }
    private void OpenDriverWebsite(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
    private void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "PocketMic-diagnostics.json", Filter = "JSON 文件|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { version = "0.1.0", connected = receiver.Connected, outputRunning = audio.Running, frames = receiver.Frames, networkRoundTripMs = receiver.RoundTripMs, bufferedMs = receiver.Buffer.BufferedMs, droppedSamples = receiver.Buffer.DroppedSamples, underruns = receiver.Buffer.Underruns, targetBufferMs = receiver.Buffer.TargetMs, muted = audio.Muted }, new JsonSerializerOptions { WriteIndented = true }));
            Notice.Text = "诊断已保存，不包含录音、IP 地址或配对密钥。";
        }
        catch (IOException ex) { Notice.Text = $"保存失败：{ex.Message}"; }
    }
    private void ShowGuide(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "第一次连接，只需三步", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "01  安装一次虚拟音频驱动\nPocketMic 会自动检测、下载并校验 VB-Audio 的 VB-CABLE 驱动，随后启动安装。请确认 Windows 管理员授权；如需重启，按提示完成。安装后自动选择虚拟设备。驱动为 Donationware，可通过界面的「支持作者」入口了解许可和捐赠。\n\n02  连接手机\n电脑和安卓手机连接同一 Wi-Fi 或手机热点。安装 PocketMic APK，在手机上扫码并授予麦克风权限。Windows 防火墙弹窗仅勾选「专用网络」。若使用 VPN，请选择手机能够访问的网卡地址。\n\n03  在应用里选择麦克风\nPocketMic 输出选择 CABLE Input；会议、游戏、OBS 的麦克风选择 CABLE Output。耳机仍作为应用的播放设备。\n\n连接不上？\n检查是否在同一局域网、路由器是否启用访客隔离；将当前网络设置为可信的专用网络。可运行发行包中的 Allow-Firewall.ps1（管理员）仅允许专用网络的本地子网访问 TCP 47655。\n\n锁屏与重连\n手机使用前台录音服务，通知栏可随时静音和停止。部分厂商需要允许后台运行；断网后保留配对并自动重连。Wi-Fi 地址变化时需要重新扫码。\n\nUSB 备选（开发者）\n启用安卓 USB 调试，运行 adb reverse tcp:47655 tcp:47655，并将配对链接的 host 改成 127.0.0.1。适用于拥挤的无线网络。", TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 23 });
        var button = new Button { Content = "打开 VB-CABLE 官方下载页 ↗", Margin = new Thickness(0, 20, 0, 0) };
        button.Click += (_, _) => Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true });
        panel.Children.Add(button);
        new Window { Title = "PocketMic 使用指南", Width = 600, Height = 750, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, FontFamily = FontFamily, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        meter.Stop(); updateTimer.Stop(); tasks.Cancel(); audio.Dispose();
        await receiver.DisposeAsync();
        identity.Dispose(); closed = true; Close();
    }
}
