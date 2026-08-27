# Structure

## Verified facts

- `Sources/MirrorPowerAI` contains the untouched macOS implementation (`AppDelegate.swift`, audio, transcription, Gemini, hotkey, keychain and overlay files).
- `Windows/src/MirrorPowerAI.Core` contains platform-independent contracts, configuration, sessions, Gemini, privacy, security and transcription coordination.
- `Windows/src/MirrorPowerAI.Windows` contains WPF, tray, Win32, WASAPI, DPAPI and Whisper adapters.
- `Windows/tests/MirrorPowerAI.Core.Tests` and `Windows/tests/MirrorPowerAI.Windows.Tests` provide deterministic Core and Windows-boundary tests.
- `Windows/tools/MirrorPowerAI.Benchmark` contains the local Whisper benchmark utility.

## Evidence

- `Windows/MirrorPowerAI.slnx` project graph.
- Directory listings under `Windows/src`, `Windows/tests` and `Windows/tools`.
- `Windows/README.md` isolation and build sections.

## Intent gap

- The Windows implementation is intentionally isolated under `Windows/`; macOS source and `Package.swift` are not edited by the Windows composition.
