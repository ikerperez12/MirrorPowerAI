# Conventions

## Verified facts

- C# projects enable nullable reference types and implicit usings.
- Public APIs use XML documentation and explicit argument validation.
- Sensitive data is kept out of diagnostic messages; failures are mapped to stable categories and localized resource keys.
- The code uses async APIs with cancellation tokens for storage, capture, transcription and HTTP operations.
- User-visible strings are stored in Spanish and English `.resx` resources and resolved by `LocalizationService`.

## Evidence

- `Windows/src/MirrorPowerAI.Core/MirrorPowerAI.Core.csproj`.
- `Windows/src/MirrorPowerAI.Core/Sessions/SessionController.cs`.
- `Windows/src/MirrorPowerAI.Windows/Resources/Strings.resx` and `Strings.en.resx`.
- `Windows/src/MirrorPowerAI.Windows/Resources/LocalizationService.cs`.

## Unknowns

- [TODO] No separate editorconfig was present in the codebase scan; preserve existing formatting and CI checks when adding files.
