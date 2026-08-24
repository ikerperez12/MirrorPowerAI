# Dependencias directas

Las versiones se fijan en `Windows/Directory.Packages.props` y se resuelven con lockfiles. La tabla debe revisarse junto con los metadatos NuGet antes de cualquier redistribución.

| Dependencia | Versión | Finalidad | Licencia declarada |
|---|---:|---|---|
| NAudio | 2.3.0 | WASAPI loopback y formatos de audio | MIT |
| Whisper.net | 1.9.1 | API .NET para whisper.cpp | MIT |
| Whisper.net.Runtime | 1.9.1 | Runtime CPU nativo | MIT |
| xunit | 2.9.3 | Pruebas | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Descubrimiento de pruebas | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | Host de pruebas | MIT |
| coverlet.collector / coverlet.msbuild | 6.0.4 | Cobertura | MIT |

Acciones CI fijadas:

| Acción | Tag auditado | Commit fijado |
|---|---|---|
| actions/checkout | v7.0.1 | `3d3c42e5aac5ba805825da76410c181273ba90b1` |
| actions/setup-dotnet | v6.0.0 | `a98b56852c35b8e3190ac28c8c2271da59106c68` |
| gitleaks/gitleaks-action | v3.0.0 | `e0c47f4f8be36e29cdc102c57e68cb5cbf0e8d1e` |

No se incluye el modelo Whisper en el repositorio ni en artefactos. Su descarga y términos deben revisarse de forma independiente antes de una distribución pública.

Cada publicación ejecuta `Windows\verify-whisper-runtime.ps1` contra el propio portable. La comprobación fuerza el runtime CPU x64, confirma que el cargador nativo y sus dependencias funcionan y no abre o descarga ningún modelo.
