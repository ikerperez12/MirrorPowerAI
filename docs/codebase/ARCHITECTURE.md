# Architecture

## Verified facts

- `SessionController` is the Core owner of session transitions and cancellation.
- Windows capture implements system-output or application-process loopback through `WasapiLoopbackAudioCaptureService`.
- Captured audio is normalized to mono, 16 kHz, 16-bit WAV by `Pcm16WaveConverter`.
- Continuous mode exposes bounded `CapturedAudio` segments through `IAudioSegmentSource`; question-like transcripts invoke the answer service while capture returns to `Capturing`.
- `Alt+Shift+L` is registered by the shell and delegates to `ISessionCommands.ToggleAsync`; in continuous mode it starts/resumes or pauses.
- The WPF overlay is shown only after `IOverlayProtectionService` verifies `WDA_EXCLUDEFROMCAPTURE`.

## Evidence

- `Windows/src/MirrorPowerAI.Core/Sessions/SessionController.cs`.
- `Windows/src/MirrorPowerAI.Core/Audio/IAudioSegmentSource.cs`.
- `Windows/src/MirrorPowerAI.Windows/Audio/WasapiLoopbackAudioCaptureService.cs`.
- `Windows/src/MirrorPowerAI.Windows/Shell/GlobalHotKeyService.cs` and `CoreSessionCommands.cs`.
- `Windows/src/MirrorPowerAI.Windows/UI/OverlayPresenter.cs` and `OverlayProtectionService.cs`.

## Intent gap

- Output loopback provides words heard by Windows, not reliable participant identity. Speaker diarization and microphone capture are not implemented.
