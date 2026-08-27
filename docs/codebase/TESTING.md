# Testing

## Verified facts

- Core tests cover configuration, audio value objects, consent/redaction, Gemini HTTP behavior, model management and `SessionController` transitions.
- Windows tests cover WASAPI adapters, shell commands, tray behavior, overlay protection, UI accessibility and platform wrappers.
- Tests use fake capture sessions, in-memory stores, fake HTTP handlers and deterministic clocks; the focused suites do not require live audio or Gemini.
- The Windows build uses warnings as errors in the repository build configuration.

## Evidence

- `Windows/tests/MirrorPowerAI.Core.Tests/*.cs`.
- `Windows/tests/MirrorPowerAI.Windows.Tests/**/*.cs`.
- `Windows/build.ps1`, `Windows/test.ps1` and `Windows/Directory.Build.props`.

## Current continuation evidence

- Focused Core suite: 103 passing tests.
- Focused Windows suite: 222 passing tests.
- Release build with `C:\Users\ijpg1\.dotnet\dotnet.exe build Windows/MirrorPowerAI.slnx -c Release --no-restore`: 0 warnings, 0 errors.

## Manual gate

- [ASK USER] Manual confirmation is still required for real browser, Teams and Discord output, API-key verification, DPI/accessibility and capture-tool behavior on the user's Windows machine.
