import app.pocketmic.*;
import javax.net.ssl.*;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;

public final class InteropClient {
    private static SSLSocket connect(Pairing p) throws Exception {
        SSLSocket socket = PinnedTls.create(p);
        socket.setEnabledProtocols(new String[] { "TLSv1.3", "TLSv1.2" });
        socket.setSoTimeout(5000); socket.setTcpNoDelay(true);
        socket.connect(new InetSocketAddress(p.host, p.port), 5000);
        try { socket.startHandshake(); return socket; }
        catch (Exception e) { socket.close(); throw e; }
    }
    private static void hello(DataOutputStream out, Pairing p, String token) throws Exception {
        String json = "{\"token\":\"" + token + "\",\"deviceName\":\"Java interop sender\",\"sampleRate\":48000,\"channels\":1,\"bits\":16,\"frameSamples\":960}";
        Wire.write(out, Wire.HELLO, 0, json.getBytes(StandardCharsets.UTF_8));
    }
    public static void main(String[] args) throws Exception {
        String uri = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8)).readLine();
        Pairing p = Pairing.parse(uri);
        Pairing wrongPin = Pairing.parse(uri.replace("fp=" + p.fingerprint, "fp=" + "0".repeat(64)));
        try (SSLSocket ignored = connect(wrongPin)) { throw new AssertionError("Wrong certificate fingerprint accepted"); }
        catch (SSLHandshakeException expected) { System.out.println("PASS: mismatched certificate rejected before authentication"); }
        try (SSLSocket socket = connect(p)) {
            DataInputStream in = new DataInputStream(socket.getInputStream()); DataOutputStream out = new DataOutputStream(socket.getOutputStream());
            hello(out, p, "0".repeat(64));
            if (Wire.read(in).type != Wire.ERROR) throw new AssertionError("Bad token accepted");
        }
        System.out.println("PASS: invalid pairing token rejected");
        for (int session = 0; session < 2; session++) {
            try (SSLSocket socket = connect(p)) {
                DataInputStream in = new DataInputStream(socket.getInputStream()); DataOutputStream out = new DataOutputStream(socket.getOutputStream());
                hello(out, p, p.token);
                if (Wire.read(in).type != Wire.ACCEPT) throw new AssertionError("Pairing rejected");
                for (int i = 0; i < (session == 0 ? 300 : 60); i++) {
                    byte[] pcm = new byte[Wire.AUDIO_BYTES];
                    for (int n = 0; n < 960; n++) {
                        short sample = (short)(Math.sin((i * 960L + n) * 2 * Math.PI * 440 / 48000) * 8000);
                        pcm[2*n] = (byte)sample; pcm[2*n+1] = (byte)(sample >> 8);
                    }
                    Wire.write(out, Wire.AUDIO, i, pcm);
                    if (i % 50 == 0) {
                        Wire.Packet ping = Wire.read(in);
                        if (ping.type != Wire.PING) throw new AssertionError("Expected heartbeat");
                        Wire.write(out, Wire.PONG, ping.sequence, ping.payload);
                    }
                    if (session > 0 || i >= 100) Thread.sleep(20);
                }
                Wire.write(out, Wire.STOP, 0, new byte[0]);
            }
            Thread.sleep(250);
        }
        System.out.println("PASS: PCM audio, burst recovery, heartbeat, disconnect and reconnect");
    }
}
