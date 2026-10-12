package app.pocketmic;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.os.*;
import android.view.View;
import android.widget.*;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

public final class MainActivity extends Activity {
    private Pairing pairing;
    private TextView status, detail, computer, address;
    private Button connect, pair, manual, stop;
    private LevelView level;
    private UpdateChecker updates;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable refresh = new Runnable() { public void run() { render(); handler.postDelayed(this, 100); } };

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved); setContentView(R.layout.activity_main);
        if (Build.VERSION.SDK_INT >= 35) {
            View root = findViewById(R.id.root);
            root.setOnApplyWindowInsetsListener((view, insets) -> {
                android.graphics.Insets bars = insets.getInsets(android.view.WindowInsets.Type.systemBars());
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom); return insets;
            });
        }
        pairing = PairingStore.load(this);
        status = findViewById(R.id.status); detail = findViewById(R.id.detail); computer = findViewById(R.id.computer); address = findViewById(R.id.address);
        connect = findViewById(R.id.connect); pair = findViewById(R.id.pair); manual = findViewById(R.id.manual); stop = findViewById(R.id.stop); level = findViewById(R.id.level);
        Button updateButton = findViewById(R.id.update);
        updates = new UpdateChecker(this, updateButton::setText);
        updateButton.setOnClickListener(v -> updates.clicked());
        connect.setOnClickListener(v -> { if (MicService.running) startService(new Intent(this, MicService.class).setAction(MicService.MUTE)); else if (pairing == null) scan(); else startMic(); });
        pair.setOnClickListener(v -> scan()); manual.setOnClickListener(v -> paste());
        stop.setOnClickListener(v -> { stopService(new Intent(this, MicService.class)); MicService.status = "连接已断开"; MicService.detail = "点击连接，即可继续使用"; });
        findViewById(R.id.help).setOnClickListener(v -> new AlertDialog.Builder(this).setTitle("连接，从这里开始")
                .setMessage("1. 在 Windows 打开 PocketMic，首次安装 VB-CABLE 虚拟音频驱动。\n\n2. 手机和电脑连接同一 Wi-Fi 或手机热点，用本应用扫描电脑二维码。\n\n3. 允许麦克风权限后点击连接。电脑端选择 CABLE Input，会议 / 游戏麦克风选择 CABLE Output。\n\n锁屏会继续录音，通知栏可静音或停止。部分手机需要允许后台运行。\n\n连不上时检查 Windows 专用网络防火墙、VPN 和路由器访客隔离。电脑 IP 改变后请重新扫码。\n\n配对保存在手机加密存储中；只有点击连接才开始采音。")
                .setNeutralButton("开源许可", (d, w) -> showLicenses()).setPositiveButton("知道了", null).show());
        if (saved == null && getIntent().getData() != null) acceptPairing(getIntent().getData().toString());
    }
    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); if (intent.getData() != null) acceptPairing(intent.getData().toString()); }
    private void scan() { if (MicService.running) return; new IntentIntegrator(this).setDesiredBarcodeFormats(IntentIntegrator.QR_CODE).setPrompt("扫描电脑端 PocketMic 二维码").setBeepEnabled(false).setOrientationLocked(false).initiateScan(); }
    private void paste() {
        EditText field = new EditText(this); field.setHint("pocketmic://pair?…"); field.setSingleLine(false); field.setTextSize(13);
        new AlertDialog.Builder(this).setTitle("粘贴电脑配对链接").setView(field).setNegativeButton("取消", null)
                .setPositiveButton("配对", (d, w) -> acceptPairing(field.getText().toString())).show();
    }
    private void acceptPairing(String uri) {
        if (MicService.running) { Toast.makeText(this, "请先断开当前连接，再配对新的电脑。", Toast.LENGTH_LONG).show(); return; }
        try {
            Pairing candidate = Pairing.parse(uri);
            // Deep links and scanned codes prepare a reviewable pairing; they never start capture.
            new AlertDialog.Builder(this).setTitle("连接这台电脑？").setMessage(candidate.name + "\n" + candidate.host + ":" + candidate.port + "\n\n请确认这是你电脑上显示的二维码。")
                    .setNegativeButton("取消", null).setPositiveButton("保存配对", (d, w) -> {
                        try { PairingStore.save(this, candidate); pairing = candidate; MicService.status = "配对成功"; MicService.detail = "点击下方按钮开始使用麦克风"; render(); }
                        catch (Exception e) { showError("无法保存配对，请重试。"); }
                    }).show();
        } catch (IllegalArgumentException e) { showError(e.getMessage()); }
    }
    private void startMic() {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO }, 100); return;
        }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                && !getPreferences(MODE_PRIVATE).getBoolean("notificationAsked", false)) {
            getPreferences(MODE_PRIVATE).edit().putBoolean("notificationAsked", true).apply();
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, 101); return;
        }
        try { startForegroundService(new Intent(this, MicService.class).setAction(MicService.START)); }
        catch (RuntimeException e) { showError("无法启动录音，请在系统设置中允许麦克风权限。"); }
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == 100) { if (results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) startMic(); else showError("需要麦克风权限才能把声音传给电脑。可在系统设置中开启。"); }
        if (request == 101) startMic();
    }
    @Override protected void onActivityResult(int request, int result, Intent data) {
        IntentResult scan = IntentIntegrator.parseActivityResult(request, result, data);
        if (scan != null) { if (scan.getContents() != null) acceptPairing(scan.getContents()); }
        else super.onActivityResult(request, result, data);
    }
    private void render() {
        if (status == null) return;
        status.setText(MicService.running && MicService.muted ? "麦克风已静音" : MicService.status);
        detail.setText(MicService.running && MicService.muted ? "已暂停采音；点击恢复，电脑即可继续听见你" : MicService.detail);
        computer.setText(pairing == null ? "还没有配对设备" : pairing.name);
        address.setText(pairing == null ? "扫描电脑端 PocketMic 的二维码" : pairing.host + " · 已保存安全配对");
        connect.setText(MicService.running ? MicService.muted ? "恢复麦克风" : "静音麦克风" : pairing == null ? "扫码连接电脑" : "连接并开启麦克风");
        pair.setEnabled(!MicService.running); manual.setEnabled(!MicService.running); stop.setVisibility(MicService.running ? View.VISIBLE : View.GONE);
        level.update(MicService.peak);
    }
    private void showError(String message) { new AlertDialog.Builder(this).setTitle("连接提示").setMessage(message).setPositiveButton("知道了", null).show(); }
    private void showLicenses() {
        StringBuilder text = new StringBuilder("PocketMic · MIT License\nCopyright (c) 2026 0xiaoyun0\n\nZXing / AndroidX / Gradle: Apache-2.0\n\n");
        try {
            for (String name : getAssets().list("licenses")) {
                text.append("\n--- ").append(name).append(" ---\n");
                try (java.io.BufferedReader reader = new java.io.BufferedReader(new java.io.InputStreamReader(getAssets().open("licenses/" + name), java.nio.charset.StandardCharsets.UTF_8))) {
                    String line; while ((line = reader.readLine()) != null) text.append(line).append('\n');
                }
            }
        } catch (java.io.IOException e) { text.append("完整许可见 GitHub 仓库 licenses 目录。"); }
        new AlertDialog.Builder(this).setTitle("开源许可").setMessage(text).setPositiveButton("关闭", null).show();
    }
    @Override protected void onResume() { super.onResume(); handler.post(refresh); if (updates != null) updates.resume(); }
    @Override protected void onPause() { handler.removeCallbacks(refresh); super.onPause(); }
    @Override protected void onDestroy() { if (updates != null) updates.close(); super.onDestroy(); }
}
