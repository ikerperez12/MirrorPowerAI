# Integrations

## Verified facts

- Gemini uses the official HTTPS `generativelanguage.googleapis.com` endpoint through a reusable `HttpClient`.
- API keys are read through `IGeminiApiKeyProvider`; Windows stores the key using the DPAPI-backed secret store.
- Whisper local uses a verified model managed by `WhisperModelManager` and a Windows runtime adapter.
- NAudio supplies endpoint enumeration and WASAPI loopback/application-loopback sessions.
- GitHub Actions defines CI for the repository in `.github/workflows/ci.yml`.

## Evidence

- `Windows/src/MirrorPowerAI.Core/Gemini/GeminiClient.cs` and `GeminiClientOptions.cs`.
- `Windows/src/MirrorPowerAI.Windows/Platform/DpapiSecretStore.cs`.
- `Windows/src/MirrorPowerAI.Windows/Transcription/WhisperModelManager.cs`.
- `Windows/src/MirrorPowerAI.Windows/Audio/NAudioLoopbackCaptureSession.cs` and `ProcessLoopbackCaptureSession.cs`.
- `.github/workflows/ci.yml`.

## Privacy boundary

- Gemini Audio is an explicit provider with a consent gate; local Whisper remains the default provider and there is no silent provider fallback.
