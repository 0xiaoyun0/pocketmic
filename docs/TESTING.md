# Verification scope

The first release is a preview. Automated protocol tests do not establish phone microphone quality, acoustic latency, virtual-driver compatibility, battery behavior, or a vendor's lock-screen behavior.

## Automated checks

- .NET tests exercise QR parsing, private-address restrictions, invalid tokens, fragmented TCP reads, truncated/oversized payload rejection, PCM decoding, mute consumption, clipping, bounded latency, TLS authentication, concurrent-client rejection, disconnect clearing, and reconnect.
- Android JUnit tests exercise the QR contract, malformed addresses and credentials, duplicate fields, USB loopback, exact network-order headers, oversized frames and truncated audio.
- Cross-language tests compile **the production Android `Pairing`, `Wire` and `PinnedTls` Java sources** and connect to the production Windows `Receiver` over a real TLS socket. They reject a changed certificate and invalid secret, then send generated PCM audio, return heartbeats, burst the sender, disconnect and reconnect. The receiver verifies nonzero decoded samples and bounded buffering.
- Android lint checks API-level correctness and app resources. The intentional custom trust-manager lint exception is documented in `android/app/lint.xml`; the trust anchor is a scanned leaf-certificate fingerprint and its fail-closed behavior is tested by interop.
- GitHub Actions rebuilds the Windows portable package and Android debug APK and runs the checks above. Release APK is signed separately with a private maintainer key.

## Verified locally for v0.1.0

- Windows app builds and launches; QR, address selection, device list and live status layout inspected.
- Windows unit/TLS integration tests and Android JVM unit tests run locally.
- Android APK build and lint run locally.
- Java-to-Windows TLS audio interop test run locally.

No Android handset or VB-CABLE was available on the development machine. **The physical phone → virtual recording device → meeting application chain remains unverified.** No end-to-end latency numbers are claimed.

## Real-device acceptance checklist

- [ ] Android 8 / 11 / 14 / 15: grant/deny microphone and notification permissions; scan and paste pairing link.
- [ ] Windows 10 / 11: install official VB-CABLE, select CABLE Input, confirm CABLE Output meter moves.
- [ ] Confirm recording in Windows Sound Recorder and input in one meeting app and one game / OBS.
- [ ] Headphones without microphones work; app playback stays on headphones.
- [ ] Speak, mute on phone, resume; verify no old speech plays after unmute.
- [ ] Lock phone for 30 minutes; confirm persistent recording notification and capture.
- [ ] Disconnect Wi-Fi for 15 seconds and restore; confirm reconnect without steadily growing latency.
- [ ] Close receiver / stop phone / revoke pairing; confirm immediate silence and microphone indicator stops when phone is stopped.
- [ ] Disable/re-enable virtual output device; verify visible error and recovery through device refresh.
- [ ] Exercise hotspot and USB reverse forwarding; measure actual acoustic or loopback latency independently of RTT.
- [ ] Test at least two hours of continuous use, thermal behavior and battery drain before claiming production readiness.
