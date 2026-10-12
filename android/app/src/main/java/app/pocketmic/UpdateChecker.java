package app.pocketmic;

import android.app.*;
import android.content.*;
import android.content.pm.*;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;
import androidx.core.content.FileProvider;
import org.json.*;
import java.io.*;
import java.net.*;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.concurrent.*;

public final class UpdateChecker implements AutoCloseable {
    public interface Listener { void changed(String message); }
    private final Activity activity;
    private final Listener listener;
    private final ScheduledExecutorService executor = Executors.newSingleThreadScheduledExecutor();
    private volatile File ready;
    private volatile boolean checking;
    private boolean waitingPermission;
    public UpdateChecker(Activity activity, Listener listener) {
        this.activity = activity; this.listener = listener;
        executor.scheduleWithFixedDelay(() -> check(false), 0, 6, TimeUnit.HOURS);
    }
    private void notifyUi(String message) { activity.runOnUiThread(() -> { if (!activity.isDestroyed()) listener.changed(message); }); }
    public void clicked() {
        if (ready != null) install();
        else if (!checking) executor.execute(() -> check(true));
    }
    private static HttpURLConnection open(String url) throws Exception {
        HttpURLConnection connection = (HttpURLConnection)new URL(url).openConnection();
        connection.setConnectTimeout(15000); connection.setReadTimeout(30000);
        connection.setRequestProperty("User-Agent", "PocketMic/0.1.0");
        if (connection.getResponseCode() != 200) { connection.disconnect(); throw new IOException("Release server unavailable"); }
        return connection;
    }
    private static String json(String url) throws Exception {
        HttpURLConnection conn = open(url);
        try (InputStream stream = conn.getInputStream(); ByteArrayOutputStream bytes = new ByteArrayOutputStream()) {
            byte[] buffer = new byte[8192]; int n;
            while ((n = stream.read(buffer)) != -1) { if (bytes.size() + n > 1_000_000) throw new IOException("Update metadata too large"); bytes.write(buffer, 0, n); }
            return bytes.toString("UTF-8");
        } finally { conn.disconnect(); }
    }
    private void check(boolean manual) {
        if (checking || ready != null) return;
        checking = true;
        try {
            if (manual) notifyUi("正在检查更新…");
            String version = BuildConfig.VERSION_NAME;
            JSONArray releases = new JSONArray(json("https://api.github.com/repos/0xiaoyun0/pocketmic/releases?per_page=20"));
            for (int i = 0; i < releases.length(); i++) {
                JSONObject release = releases.getJSONObject(i);
                if (release.optBoolean("draft")) continue;
                String tag = release.optString("tag_name");
                try { if (Versioning.newer(tag, version)) version = tag.replaceFirst("^v", ""); } catch (IllegalArgumentException ignored) { }
            }
            if (!Versioning.newer(version, BuildConfig.VERSION_NAME)) { notifyUi("v" + BuildConfig.VERSION_NAME + " · 已是最新版本"); return; }
            JSONObject manifest = new JSONObject(json(Versioning.assetUrl(version, "update-manifest.json")));
            if (manifest.getInt("schema") != 1 || !version.equals(manifest.getString("version"))) throw new IOException("Release manifest mismatch");
            JSONObject asset = manifest.getJSONObject("android");
            String name = asset.getString("name"), hash = asset.getString("sha256"); long size = asset.getLong("size");
            String url = Versioning.assetUrl(version, name);
            if (!hash.matches("[0-9A-Fa-f]{64}") || size <= 0 || size > 100_000_000) throw new IOException("Invalid release asset");
            notifyUi("发现 v" + version + " · 正在后台下载");
            File folder = new File(activity.getFilesDir(), "updates");
            if (!folder.isDirectory() && !folder.mkdirs()) throw new IOException("Cannot create update directory");
            File file = new File(folder, name);
            HttpURLConnection conn = open(url);
            MessageDigest digest = MessageDigest.getInstance("SHA-256"); long total = 0;
            try (InputStream in = conn.getInputStream(); OutputStream out = new FileOutputStream(file)) {
                byte[] buffer = new byte[65536]; int n;
                while ((n = in.read(buffer)) != -1) {
                    if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException();
                    total += n; if (total > size) throw new IOException("APK size exceeds manifest");
                    out.write(buffer, 0, n); digest.update(buffer, 0, n);
                }
            } finally { conn.disconnect(); }
            if (total != size || !MessageDigest.isEqual(digest.digest(), Pairing.fromHex(hash))) throw new IOException("APK checksum mismatch");
            verifyApk(file, version);
            ready = file;
            notifyUi("v" + version + " 已下载 · 点击安装更新");
        } catch (Exception e) { notifyUi(manual ? "更新检查失败 · 点击重试" : "自动更新待网络恢复后重试"); }
        finally { checking = false; }
    }
    @SuppressWarnings("deprecation")
    private void verifyApk(File file, String version) throws Exception {
        int flags = Build.VERSION.SDK_INT >= 28 ? PackageManager.GET_SIGNING_CERTIFICATES : PackageManager.GET_SIGNATURES;
        PackageManager manager = activity.getPackageManager();
        PackageInfo installed = manager.getPackageInfo(activity.getPackageName(), flags);
        PackageInfo archive = manager.getPackageArchiveInfo(file.getAbsolutePath(), flags);
        if (archive == null || !activity.getPackageName().equals(archive.packageName) || !version.equals(archive.versionName)
                || (Build.VERSION.SDK_INT >= 28 ? archive.getLongVersionCode() <= installed.getLongVersionCode() : archive.versionCode <= installed.versionCode)) throw new SecurityException("Unexpected APK identity/version");
        Signature[] current = Build.VERSION.SDK_INT >= 28 ? installed.signingInfo.getApkContentsSigners() : installed.signatures;
        Signature[] next = Build.VERSION.SDK_INT >= 28 ? archive.signingInfo.getApkContentsSigners() : archive.signatures;
        if (current.length != 1 || next.length != 1 || !Arrays.equals(current[0].toByteArray(), next[0].toByteArray())) throw new SecurityException("APK signing certificate changed");
    }
    private void install() {
        if (ready == null) return;
        if (MicService.running) { new AlertDialog.Builder(activity).setMessage("请先断开麦克风，再安装更新，避免中断通话。").setPositiveButton("知道了", null).show(); return; }
        if (!activity.getPackageManager().canRequestPackageInstalls()) {
            waitingPermission = true;
            activity.startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + activity.getPackageName())));
            return;
        }
        Uri uri = FileProvider.getUriForFile(activity, activity.getPackageName() + ".updates", ready);
        activity.startActivity(new Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION));
    }
    public void resume() { if (waitingPermission && activity.getPackageManager().canRequestPackageInstalls()) { waitingPermission = false; install(); } }
    @Override public void close() { executor.shutdownNow(); }
}
