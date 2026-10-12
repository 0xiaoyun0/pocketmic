package app.pocketmic;

import org.junit.Test;
import java.io.*;
import static org.junit.Assert.*;

public class ProtocolTest {
    private static final String KEY = "A".repeat(64);
    private String link(String host) { return "pocketmic://pair?v=1&host=" + host + "&port=47655&key=" + KEY + "&fp=" + KEY + "&name=%E7%94%B5%E8%84%91"; }
    @Test public void parsesSameQrContractAsWindows() {
        Pairing p = Pairing.parse(link("192.168.1.4")); assertEquals("电脑", p.name); assertEquals(47655, p.port); assertEquals(KEY, p.fingerprint);
    }
    @Test public void rejectsNonLocalAndInvalidAddresses() {
        for (String host : new String[] { "8.8.8.8", "example.com", "192.168.999.1", "0.0.0.0", "192.168.01.1" }) {
            assertThrows(IllegalArgumentException.class, () -> Pairing.parse(link(host)));
        }
    }
    @Test public void rejectsBadOrDuplicateCredentials() {
        assertThrows(IllegalArgumentException.class, () -> Pairing.parse(link("10.0.0.1").replace("key=" + KEY, "key=123")));
        assertThrows(IllegalArgumentException.class, () -> Pairing.parse(link("10.0.0.1") + "&key=" + KEY));
    }
    @Test public void usbLoopbackAllowed() { assertEquals("127.0.0.1", Pairing.parse(link("127.0.0.1")).host); }
    @Test public void packetHeaderMatchesWindowsContract() throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        Wire.write(new DataOutputStream(bytes), Wire.AUDIO, 123, new byte[1920]);
        byte[] header = java.util.Arrays.copyOf(bytes.toByteArray(), 16);
        assertArrayEquals(new byte[] {80,77,73,67,1,3,0,0,0,0,0,123,0,0,7,(byte)128}, header);
        Wire.Packet packet = Wire.read(new DataInputStream(new ByteArrayInputStream(bytes.toByteArray())));
        assertEquals(123, packet.sequence); assertEquals(1920, packet.payload.length);
    }
    @Test public void rejectsOversizedPacketBeforeAllocation() {
        byte[] header = {80,77,73,67,1,1,0,0,0,0,0,0,127,-1,-1,-1};
        assertThrows(IOException.class, () -> Wire.read(new DataInputStream(new ByteArrayInputStream(header))));
    }
    @Test public void rejectsTruncatedAudio() throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream(); Wire.write(new DataOutputStream(bytes), Wire.AUDIO, 0, new byte[1920]);
        byte[] truncated = java.util.Arrays.copyOf(bytes.toByteArray(), 1900);
        assertThrows(EOFException.class, () -> Wire.read(new DataInputStream(new ByteArrayInputStream(truncated))));
    }
}
