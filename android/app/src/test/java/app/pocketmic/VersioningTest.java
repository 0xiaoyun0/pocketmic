package app.pocketmic;
import org.junit.Test;
import static org.junit.Assert.*;
public class VersioningTest {
    @Test public void numericUpdateOrder() { assertTrue(Versioning.newer("v0.10.0", "0.9.9")); assertFalse(Versioning.newer("0.1.0", "0.1.0")); assertFalse(Versioning.newer("0.1.0", "1.0.0")); }
    @Test public void rejectsInvalidVersionAndPathTraversal() {
        for (String value : new String[] { "v1.0.0/../../evil", "01.2.3", "1.2", "1.2.3.4" }) assertThrows(IllegalArgumentException.class, () -> Versioning.parse(value));
        assertThrows(IllegalArgumentException.class, () -> Versioning.assetUrl("0.1.0", "https://evil.example/app.apk"));
    }
    @Test public void releaseUsesOnlyTheConfiguredRepository() {
        assertEquals("https://github.com/0xiaoyun0/pocketmic/releases/download/v0.2.0/PocketMic-0.2.0-android.apk", Versioning.assetUrl("0.2.0", "PocketMic-0.2.0-android.apk"));
    }
}
