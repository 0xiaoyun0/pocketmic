package app.pocketmic;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.*;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.NoiseSuppressor;
import android.net.wifi.WifiManager;
import android.os.*;
import org.json.JSONObject;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicLong;
import javax.net.ssl.*;

public final class MicService extends Service {
    public static final String START = "app.pocketmic.START", STOP = "app.pocketmic.STOP", MUTE = "app.pocketmic.MUTE";
    public static volatile boolean running, connected, muted;
    public static volatile float peak;
    public static volatile String status = "准备好，让电脑听见你", detail = "同一 Wi-Fi · 扫码配对 · 一键连接";
    private volatile boolean stopping;
    private static volatile MicService owner;
    private volatile Socket socket;
    private volatile AudioRecord activeRecorder;
    private Thread worker;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;
    private final Handler main = new Handler(Looper.getMainLooper());
    private static final String CHANNEL = "microphone";
    private static final class FatalConnection extends IOException { FatalConnection(String message) { super(message); } }
    private static final class Frame {
        final byte[] pcm; final long captured = SystemClock.elapsedRealtime();
        Frame(byte[] pcm) { this.pcm = pcm; }
    }

    @Override public void onCreate() {
        super.onCreate();
        NotificationChannel channel = new NotificationChannel(CHANNEL, "麦克风连接", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("显示录音状态，随时静音或停止");
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? STOP : intent.getAction();
        if (STOP.equals(action)) { shutdown(); stopSelf(); return START_NOT_STICKY; }
        if (MUTE.equals(action)) {
            if (running) { muted = !muted; updateNotification(); }
            else stopSelf();
            return START_NOT_STICKY;
        }
        if (running) return START_NOT_STICKY;
        if (worker != null && worker.isAlive()) return START_NOT_STICKY;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            status = "请允许麦克风权限"; stopSelf(); return START_NOT_STICKY;
        }
        Pairing pairing = PairingStore.load(this);
        if (pairing == null) { status = "请先扫描电脑二维码"; stopSelf(); return START_NOT_STICKY; }
        try {
            if (Build.VERSION.SDK_INT >= 30) startForeground(1, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
            else startForeground(1, notification());
            owner = this; running = true; connected = false; muted = false; stopping = false;
            status = "正在连接电脑…"; detail = "正在建立加密连接";
            acquireLocks();
            worker = new Thread(() -> connectionLoop(pairing), "PocketMic-connection"); worker.start();
        } catch (RuntimeException e) {
            status = "无法启动录音服务"; detail = "请打开应用并允许麦克风权限后重试。"; shutdown(); stopSelf();
        }
        // A stopped/killed service must never restart microphone capture without a fresh user action.
        return START_NOT_STICKY;
    }

    private void connectionLoop(Pairing pairing) {
        int delay = 500;
        try {
            while (!stopping) {
                try { stream(pairing); delay = 500; }
                catch (FatalConnection e) { status = "需要重新连接"; detail = e.getMessage(); break; }
                catch (Exception e) {
                    if (stopping) break;
                    status = "连接中断，正在重连…";
                    detail = "请检查电脑端、Wi-Fi 和防火墙；恢复网络后自动继续。";
                }
                finally { if (owner == this) { connected = false; peak = 0; } closeSocket(); main.post(this::updateNotification); }
                if (!stopping) { Thread.sleep(delay); delay = Math.min(delay * 2, 5000); }
            }
        } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
        finally { main.post(() -> { shutdown(); stopSelf(); }); }
    }

    @SuppressLint("MissingPermission")
    private void stream(Pairing pairing) throws Exception {
        SSLSocket conn = PinnedTls.create(pairing);
        socket = conn;
        if (stopping) { conn.close(); return; }
        conn.setEnabledProtocols(Build.VERSION.SDK_INT >= 29 ? new String[] { "TLSv1.3", "TLSv1.2" } : new String[] { "TLSv1.2" });
        conn.setTcpNoDelay(true); conn.setSendBufferSize(8192); conn.setSoTimeout(8000);
        conn.connect(new InetSocketAddress(pairing.host, pairing.port), 5000);
        try { conn.startHandshake(); }
        catch (SSLHandshakeException e) { throw new FatalConnection("电脑证书不匹配或已过期，请检查手机时间并重新扫码。"); }
        DataInputStream input = new DataInputStream(conn.getInputStream());
        DataOutputStream output = new DataOutputStream(conn.getOutputStream());
        JSONObject hello = new JSONObject().put("token", pairing.token).put("deviceName", Build.MANUFACTURER + " " + Build.MODEL)
                .put("sampleRate", Wire.RATE).put("channels", 1).put("bits", 16).put("frameSamples", Wire.SAMPLES);
        Wire.write(output, Wire.HELLO, 0, hello.toString().getBytes(StandardCharsets.UTF_8));
        Wire.Packet accepted = Wire.read(input);
        if (accepted.type == Wire.ERROR) throw new FatalConnection(new JSONObject(new String(accepted.payload, StandardCharsets.UTF_8)).optString("message", "配对失效，请重新扫码。"));
        if (accepted.type != Wire.ACCEPT) throw new FatalConnection("电脑端协议不兼容，请更新两端应用。");

        int minimum = AudioRecord.getMinBufferSize(Wire.RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
        if (minimum <= 0) throw new FatalConnection("手机不支持 48 kHz 录音。");
        AudioRecord recorder = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, Wire.RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(minimum, Wire.AUDIO_BYTES * 4));
        if (recorder.getState() != AudioRecord.STATE_INITIALIZED) { recorder.release(); throw new FatalConnection("麦克风不可用，请关闭占用麦克风的其他应用。"); }
        activeRecorder = recorder;
        for (AudioDeviceInfo info : getSystemService(AudioManager.class).getDevices(AudioManager.GET_DEVICES_INPUTS)) {
            if (info.getType() == AudioDeviceInfo.TYPE_BUILTIN_MIC) { recorder.setPreferredDevice(info); break; }
        }
        NoiseSuppressor ns = null; AcousticEchoCanceler aec = null;
        AtomicBoolean alive = new AtomicBoolean(true);
        AtomicLong lastSent = new AtomicLong(SystemClock.elapsedRealtime());
        ScheduledExecutorService watchdog = Executors.newSingleThreadScheduledExecutor();
        ArrayBlockingQueue<Frame> queue = new ArrayBlockingQueue<>(3);
        Thread writer = null, reader = null;
        try {
            if (NoiseSuppressor.isAvailable()) { ns = NoiseSuppressor.create(recorder.getAudioSessionId()); if (ns != null) ns.setEnabled(true); }
            if (AcousticEchoCanceler.isAvailable()) { aec = AcousticEchoCanceler.create(recorder.getAudioSessionId()); if (aec != null) aec.setEnabled(true); }
            recorder.startRecording();
            if (recorder.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING) throw new FatalConnection("无法开始录音，请检查麦克风权限。");
            connected = true; status = "电脑正在听见你"; detail = "48 kHz · 加密连接 · 可锁屏使用"; main.post(this::updateNotification);
            writer = new Thread(() -> {
                int sequence = 0;
                try {
                    while (alive.get() && !stopping) {
                        Frame frame = queue.poll(1, TimeUnit.SECONDS);
                        if (frame == null) continue;
                        if (SystemClock.elapsedRealtime() - frame.captured > 120) continue;
                        if (muted) Arrays.fill(frame.pcm, (byte)0);
                        Wire.write(output, Wire.AUDIO, sequence++, frame.pcm);
                        lastSent.set(SystemClock.elapsedRealtime());
                    }
                } catch (Exception ignored) { }
                finally { alive.set(false); try { conn.close(); } catch (IOException ignored) { } }
            }, "PocketMic-send");
            reader = new Thread(() -> {
                try {
                    while (alive.get() && !stopping) {
                        Wire.Packet packet = Wire.read(input);
                        if (packet.type == Wire.PING && packet.payload.length == 8) Wire.write(output, Wire.PONG, packet.sequence, packet.payload);
                        else throw new IOException("Unexpected server packet");
                    }
                } catch (Exception ignored) { }
                finally { alive.set(false); try { conn.close(); } catch (IOException ignored) { } }
            }, "PocketMic-control");
            // SO_TIMEOUT covers reads, not a blocked write or a reader waiting for the writer lock.
            // Closing the socket from an independent watchdog breaks both and enables reconnect.
            watchdog.scheduleAtFixedRate(() -> {
                if (alive.get() && SystemClock.elapsedRealtime() - lastSent.get() > 5000) {
                    alive.set(false); try { conn.close(); } catch (IOException ignored) { }
                    stopRecorder();
                }
            }, 1, 1, TimeUnit.SECONDS);
            writer.start(); reader.start();
            short[] samples = new short[Wire.SAMPLES];
            boolean recording = true;
            while (!stopping && alive.get()) {
                if (muted && recording) { recorder.stop(); recording = false; }
                if (!muted && !recording) { recorder.startRecording(); recording = true; }
                if (!recording) { Arrays.fill(samples, (short)0); Thread.sleep(20); }
                else {
                    int offset = 0;
                    while (offset < samples.length && alive.get() && !stopping) {
                        int read = recorder.read(samples, offset, samples.length - offset, AudioRecord.READ_BLOCKING);
                        if (read <= 0) throw new IOException("录音设备暂时不可用。");
                        offset += read;
                    }
                    // A mute pressed during a blocking read also suppresses that in-flight frame.
                    if (muted) Arrays.fill(samples, (short)0);
                }
                float maximum = 0;
                byte[] bytes = new byte[Wire.AUDIO_BYTES];
                for (int i = 0; i < samples.length; i++) {
                    maximum = Math.max(maximum, Math.abs(samples[i] / 32768f));
                    bytes[i * 2] = (byte)samples[i]; bytes[i * 2 + 1] = (byte)(samples[i] >> 8);
                }
                peak = maximum;
                Frame frame = new Frame(bytes);
                if (!queue.offer(frame)) { queue.poll(); queue.offer(frame); }
            }
        } finally {
            alive.set(false); watchdog.shutdownNow(); conn.close();
            stopRecorder();
            if (ns != null) ns.release(); if (aec != null) aec.release(); recorder.release();
            if (activeRecorder == recorder) activeRecorder = null;
            if (writer != null) { writer.interrupt(); writer.join(1500); }
            if (reader != null) { reader.interrupt(); reader.join(1500); }
        }
    }

    @SuppressLint("WakelockTimeout")
    private void acquireLocks() {
        wakeLock = getSystemService(PowerManager.class).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "PocketMic:Streaming");
        wakeLock.acquire();
        WifiManager wifi = (WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);
        wifiLock = wifi.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "PocketMic:Wifi"); wifiLock.acquire();
    }
    private Notification notification() {
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent mute = PendingIntent.getService(this, 1, new Intent(this, MicService.class).setAction(MUTE), PendingIntent.FLAG_IMMUTABLE);
        PendingIntent stop = PendingIntent.getService(this, 2, new Intent(this, MicService.class).setAction(STOP), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_mic).setContentTitle("PocketMic · " + (muted ? "麦克风已静音" : connected ? "麦克风连接中" : "等待连接电脑"))
                .setContentText("点击打开；可随时静音或停止录音").setContentIntent(open).setOngoing(true)
                .addAction(new Notification.Action.Builder(null, muted ? "恢复" : "静音", mute).build())
                .addAction(new Notification.Action.Builder(null, "停止", stop).build()).build();
    }
    private void updateNotification() { if (running && !stopping) getSystemService(NotificationManager.class).notify(1, notification()); }
    private void closeSocket() { Socket current = socket; if (current != null) try { current.close(); } catch (IOException ignored) { } }
    private void stopRecorder() {
        AudioRecord current = activeRecorder;
        if (current != null) try { if (current.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) current.stop(); } catch (IllegalStateException ignored) { }
    }
    private void shutdown() {
        stopping = true;
        if (owner == this) { running = false; connected = false; peak = 0; }
        closeSocket(); stopRecorder(); if (worker != null) worker.interrupt();
        if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
        stopForeground(STOP_FOREGROUND_REMOVE);
    }
    @Override public void onDestroy() { shutdown(); super.onDestroy(); }
    @Override public IBinder onBind(Intent intent) { return null; }
}
