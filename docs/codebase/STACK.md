# Stack

## Verified facts

- macOS is a Swift Package Manager executable declared in `Package.swift` and targets macOS 14.2.
- Windows targets `net10.0` for the Core project and `net10.0-windows` for the WPF shell (`Windows/src/*/*.csproj`).
- SDK resolution is pinned to .NET `10.0.400` with roll-forward disabled in `global.json`.
- Windows dependencies include NAudio, Whisper.net and xUnit; exact versions are centrally controlled by the Windows dependency files.

## Evidence

- `Package.swift`: Swift tools version, platform and executable target.
- `global.json`: SDK version and roll-forward policy.
- `Windows/src/MirrorPowerAI.Core/MirrorPowerAI.Core.csproj` and `Windows/src/MirrorPowerAI.Windows/MirrorPowerAI.Windows.csproj`: target frameworks and nullable/implicit-using settings.
- `Windows/Directory.Packages.props`: centrally managed package versions.

## Unknowns

- [TODO] The reference scan did not identify a root-level formatter configuration; the effective CI formatting command should remain the source of truth.
