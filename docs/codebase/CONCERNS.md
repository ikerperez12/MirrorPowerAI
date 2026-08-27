# Concerns and open decisions

## Verified risks

- Windows output loopback cannot identify which participant spoke; it only receives rendered output audio.
- Application-process loopback depends on how each application exposes audio sessions; the README documents a known Teams desktop limitation and recommends system output.
- `WDA_EXCLUDEFROMCAPTURE` is a public Windows capture-affinity control, not a DRM or physical-camera guarantee.
- The continuous listener uses bounded segment queues and an eight-hour session cap; a long meeting may still need a manual pause/resume cycle.
- The first local Whisper start may wait for a visible, pre-capture model download and initialization step; spoken meeting audio is not opened until that preparation succeeds.

## Evidence

- `Windows/README.md` limits and first-use sections.
- `Windows/docs/PRIVACY.md`, `ARCHITECTURE.md` and `TROUBLESHOOTING.md`.
- `Windows/src/MirrorPowerAI.Windows/Audio/WasapiLoopbackAudioCaptureService.cs` segment and duration bounds.
- `Windows/src/MirrorPowerAI.Windows/UI/OverlayProtectionService.cs`.

## Required before calling stable

- [ASK USER] Confirm real manual operation on the target Windows installation with browser audio, Teams and Discord.
- [TODO] Decide whether participant diarization/microphone support belongs in a later scope; it is not a safe assumption for this Windows v1.
- [TODO] Obtain upstream permission/license before publishing redistributable releases.
