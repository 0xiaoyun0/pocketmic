package app.pocketmic;

public final class Versioning {
    public static int[] parse(String text) {
        if (text == null || !text.matches("v?(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)")) throw new IllegalArgumentException("Invalid release version");
        String[] parts = text.replaceFirst("^v", "").split("\\.");
        return new int[] { Integer.parseInt(parts[0]), Integer.parseInt(parts[1]), Integer.parseInt(parts[2]) };
    }
    public static boolean newer(String next, String current) {
        int[] a = parse(next), b = parse(current);
        for (int i = 0; i < 3; i++) if (a[i] != b[i]) return a[i] > b[i];
        return false;
    }
    public static String assetUrl(String version, String name) {
        parse(version);
        if (!name.equals("update-manifest.json") && !name.equals("PocketMic-" + version + "-android.apk")) throw new IllegalArgumentException("Unexpected update asset");
        return "https://github.com/0xiaoyun0/pocketmic/releases/download/v" + version + "/" + name;
    }
}
