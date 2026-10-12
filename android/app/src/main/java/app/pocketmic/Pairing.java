package app.pocketmic;

import java.net.URI;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

public final class Pairing {
    public final String host, token, fingerprint, name, uri;
    public final int port;
    private Pairing(String host, int port, String token, String fingerprint, String name, String uri) {
        this.host = host; this.port = port; this.token = token; this.fingerprint = fingerprint; this.name = name; this.uri = uri;
    }
    public static Pairing parse(String input) {
        try {
            URI uri = URI.create(input.trim());
            if (!"pocketmic".equals(uri.getScheme()) || !"pair".equals(uri.getHost())) throw new IllegalArgumentException();
            Map<String, String> values = new HashMap<>();
            for (String piece : uri.getRawQuery().split("&")) {
                String[] parts = piece.split("=", 2);
                if (parts.length != 2 || values.put(parts[0], URLDecoder.decode(parts[1], "UTF-8")) != null) throw new IllegalArgumentException();
            }
            String host = values.get("host"), key = values.get("key"), fp = values.get("fp"), name = values.get("name");
            int port = Integer.parseInt(values.get("port"));
            if (!"1".equals(values.get("v")) || !isLocal(host) || port < 1024 || port > 65535 || key == null || !key.matches("[0-9a-fA-F]{64}")
                    || fp == null || !fp.matches("[0-9a-fA-F]{64}") || name == null || name.length() > 100) throw new IllegalArgumentException();
            return new Pairing(host, port, key, fp.toUpperCase(Locale.ROOT), name, input.trim());
        } catch (Exception e) { throw new IllegalArgumentException("配对链接无效，请扫描电脑端 PocketMic 的二维码。"); }
    }
    private static boolean isLocal(String host) {
        if (host == null || !host.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}")) return false;
        String[] pieces = host.split("\\."); int[] b = new int[4];
        for (int i = 0; i < 4; i++) { b[i] = Integer.parseInt(pieces[i]); if (b[i] > 255 || (pieces[i].length() > 1 && pieces[i].startsWith("0"))) return false; }
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254);
    }
    public static byte[] fromHex(String value) {
        byte[] bytes = new byte[value.length() / 2];
        for (int i = 0; i < bytes.length; i++) bytes[i] = (byte)Integer.parseInt(value.substring(i * 2, i * 2 + 2), 16);
        return bytes;
    }
}
