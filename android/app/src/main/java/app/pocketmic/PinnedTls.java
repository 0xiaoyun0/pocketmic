package app.pocketmic;

import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import javax.net.ssl.*;

/** Trust ONLY the leaf certificate whose SHA-256 fingerprint was scanned on the user's PC.
 * The QR is the trust anchor: no system CA, trust-all fallback, or hostname override is used. */
public final class PinnedTls {
    public static SSLSocket create(Pairing pairing) throws Exception {
        final byte[] expected = Pairing.fromHex(pairing.fingerprint);
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new TrustManager[] { new X509TrustManager() {
            @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            @Override public void checkClientTrusted(X509Certificate[] chain, String auth) throws CertificateException { throw new CertificateException("Client certificates are not accepted."); }
            @Override public void checkServerTrusted(X509Certificate[] chain, String auth) throws CertificateException {
                try {
                    if (chain == null || chain.length == 0) throw new CertificateException("Missing server certificate.");
                    chain[0].checkValidity();
                    byte[] actual = MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
                    if (!MessageDigest.isEqual(actual, expected)) throw new CertificateException("Scanned certificate fingerprint mismatch.");
                } catch (CertificateException e) { throw e; }
                catch (Exception e) { throw new CertificateException("Cannot validate the paired computer.", e); }
            }
        } }, null);
        return (SSLSocket)context.getSocketFactory().createSocket();
    }
}
