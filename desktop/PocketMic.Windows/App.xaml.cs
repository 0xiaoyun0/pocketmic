using System.Windows;

namespace PocketMic.Windows;

public partial class App : Application
{
    private Mutex? single;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await UpdateService.ApplyAsync(e.Args[1]); Shutdown(); }
            catch (Exception ex) { MessageBox.Show($"更新未完成，旧版本已尽可能保留：{ex.Message}", "PocketMic 更新"); Shutdown(1); }
            return;
        }
        single = new Mutex(true, "Local\\PocketMic.Receiver", out var created);
        if (!created) { MessageBox.Show("PocketMic 已在运行，请切换到已有窗口。", "PocketMic"); Shutdown(); return; }
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { MessageBox.Show($"启动失败：{ex.Message}\n请检查 47655 端口是否被占用。", "PocketMic"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { single?.Dispose(); base.OnExit(e); }
}
