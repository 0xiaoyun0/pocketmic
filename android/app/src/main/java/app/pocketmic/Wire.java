package app.pocketmic;

import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;

public final class Wire {
    public static final int HELLO = 1, ACCEPT = 2, AUDIO = 3, PING = 4, PONG = 5, STOP = 6, ERROR = 7;
    public static final int RATE = 48000, SAMPLES = 960, AUDIO_BYTES = 1920, MAX_PAYLOAD = 8192;
    public static final class Packet {
        public final int type, sequence; public final byte[] payload;
        Packet(int type, int sequence, byte[] payload) { this.type = type; this.sequence = sequence; this.payload = payload; }
    }
    public static Packet read(DataInputStream input) throws IOException {
        if (input.readInt() != 0x504D4943 || input.readUnsignedByte() != 1) throw new IOException("协议版本不兼容。");
        int type = input.readUnsignedByte();
        if (input.readUnsignedShort() != 0 || type < 1 || type > 7) throw new IOException("无效的数据头。");
        int seq = input.readInt(), size = input.readInt();
        if (size < 0 || size > MAX_PAYLOAD || (type == AUDIO && size != AUDIO_BYTES)) throw new IOException("无效的音频数据。");
        byte[] payload = new byte[size]; input.readFully(payload);
        return new Packet(type, seq, payload);
    }
    public static void write(DataOutputStream output, int type, int seq, byte[] payload) throws IOException {
        if (payload.length > MAX_PAYLOAD) throw new IOException("Payload too large");
        synchronized (output) {
            output.writeInt(0x504D4943); output.writeByte(1); output.writeByte(type); output.writeShort(0);
            output.writeInt(seq); output.writeInt(payload.length); output.write(payload); output.flush();
        }
    }
}
