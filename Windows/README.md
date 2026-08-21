# MirrorPowerAI para Windows

Implementación nativa para Windows 11 x64, aislada de la aplicación macOS original. Esta rama es una entrega de código fuente para validación; no publica binarios ni modifica la línea Swift del proyecto.

## Estado

- Rama de desarrollo: `feat/windows-native`.
- Plataforma objetivo: Windows 11 24H2/25H2 x64.
- Runtime: WPF sobre .NET 10 LTS, SDK fijado en `10.0.400`.
- Audio: salida predeterminada del sistema mediante WASAPI loopback.
- Transcripción predeterminada: Whisper local.
- Transcripción opcional: Gemini Audio, sólo después de consentimiento explícito.
- Hotkey global: `Alt+Shift+L`.

No se distribuyen releases ni instaladores porque el repositorio upstream no declara una licencia. Consulta la [documentación de GitHub sobre repositorios sin licencia](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository).

## Requisitos

- Windows 11 x64, build 26100 o posterior.
- Microsoft Visual C++ Redistributable 2015-2022 x64.
- Git.
- .NET SDK 10.0.400. Si falta, el script firmado de Microsoft puede instalarlo sin elevación:

```powershell
.\Windows\install-dotnet.ps1
```

## Compilar, probar y publicar localmente

Desde la raíz del repositorio:

```powershell
.\Windows\build.ps1
```

El script ejecuta preflight, restore bloqueado, build Release, xUnit, cobertura del núcleo y publicación autocontenida `win-x64`. La salida local queda en `Windows\artifacts\win-x64` y no se versiona; el equipo de destino no necesita instalar el runtime de .NET. Cada publicación genera `build-provenance.json` con commit, estado limpio/sucio, SDK, RID, hash SHA-256 del ejecutable e inventario ordenado de todos los ficheros del portable con un hash agregado. El manifiesto no se incluye en su propio inventario.

Para un candidato local que vaya a someterse a QA de distribución, usa la compuerta interactiva:

```powershell
.\Windows\build.ps1 -ReleaseGate
```

Además del flujo anterior, exige un árbol Git limpio, publica el ejecutable con su manifiesto de procedencia, verifica en una ventana WPF real que `WDA_EXCLUDEFROMCAPTURE` se puede aplicar y leer, y comprueba bandeja, mutex y `Alt+Shift+L` con recursos Windows reales. Requiere una sesión local e interactiva de Windows con DWM y la aplicación normal cerrada; se rechaza deliberadamente en CI y no sustituye las pruebas con Snipping Tool, OBS, Teams o Meet. No crea una release de GitHub ni autoriza redistribución de binarios.

Para comprobar después que una copia local del portable sigue siendo exactamente la que describe su manifiesto, sin usar red, audio, configuración ni secretos:

```powershell
.\Windows\verify-provenance.ps1
```

El verificador sólo acepta una carpeta bajo `Windows\artifacts`, rechaza enlaces simbólicos, junctions y otros reparse points, y falla si encuentra un fichero añadido, omitido o modificado.

Comandos parciales:

```powershell
.\Windows\preflight.ps1
.\Windows\test.ps1
.\Windows\publish.ps1
.\Windows\verify-provenance.ps1
.\Windows\benchmark.ps1 --help
.\Windows\verify-overlay.ps1
.\Windows\verify-shell.ps1
.\Windows\verify-wasapi.ps1
```

`verify-shell.ps1` comprueba de forma aislada la bandeja, el mutex de instancia única y el registro/liberación de `Alt+Shift+L` usando recursos Windows reales. No crea configuración ni usa DPAPI; tampoco inicia audio, carga modelos ni realiza peticiones de red. Requiere una sesión local interactiva y una aplicación normal cerrada; no sustituye comprobar visualmente el icono ni pulsar físicamente el atajo durante la QA manual.

`verify-wasapi.ps1` es una prueba diagnóstica explícita del dispositivo de salida predeterminado: captura tres segundos sólo en memoria, valida que el resultado sea WAV PCM mono de 16 kHz/16 bits y lo borra antes de salir. No usa red, modelo, configuración, API key ni crea archivos de audio. Sin opciones acepta silencio si llegaron muestras; si se está reproduciendo audio autorizado y se quiere exigir señal real, ejecuta `./Windows/verify-wasapi.ps1 -RequireAudibleSignal`. No se invoca desde CI ni desde `-ReleaseGate`, para no iniciar una captura sin una intención explícita del operador.

## Primera ejecución

1. Abre `MirrorPowerAI.Windows.exe` desde la carpeta publicada.
2. En la bandeja, abre **Configuración**.
3. Introduce la API key de Gemini. Se protege con DPAPI para el usuario actual.
4. Revisa el contexto del proyecto, idioma y dispositivo.
5. Mantén **Whisper local** para máxima privacidad o selecciona **Gemini Audio** y acepta el consentimiento de nube.
6. Pulsa `Alt+Shift+L` para iniciar la captura y vuelve a pulsarlo para detenerla.

La primera transcripción local descarga el modelo fijado:

- Archivo: `ggml-base.bin`
- Tamaño: `147951465` bytes
- SHA-256: `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`
- Revisión: `5359861c739e955e79d9a303bcbc70fb988958b1`

El modelo sólo se activa después de verificar tamaño y hash. Una descarga incompleta o manipulada se descarta.

## Privacidad

- `LocalWhisper`: el audio y la transcripción permanecen en el equipo; únicamente la pregunta transcrita y el contexto se envían a Gemini para producir la respuesta.
- `GeminiAudio`: el WAV capturado se envía a Gemini para transcribirlo, tras consentimiento explícito; después se realiza la misma petición textual de respuesta.
- No existe fallback automático de local a nube.
- Audio, transcripciones y respuestas viven en memoria y no se guardan como historial.
- La API key se protege con DPAPI `CurrentUser`; la configuración no sensible se guarda en `%LOCALAPPDATA%\MirrorPowerAI`.
- No hay telemetría.

Consulta [PRIVACY.md](docs/PRIVACY.md) para el flujo de datos completo.

## Límites conocidos

- Sólo captura la mezcla de salida global, no micrófono ni un proceso concreto.
- WASAPI puede omitir audio protegido por DRM.
- `WDA_EXCLUDEFROMCAPTURE` protege frente a APIs públicas que respetan DWM; no protege frente a una cámara física, drivers o software que eluda ese mecanismo.
- Windows 10, ARM64, MSIX, firma de código, autoactualización y publicación de releases quedan fuera de esta primera versión.
- La etiqueta estable requiere completar la matriz manual de [QA_CHECKLIST.md](docs/QA_CHECKLIST.md).

## Benchmark de Whisper local

La utilidad `MirrorPowerAI.Benchmark` mide WER y RTF con WAV PCM mono de 16 kHz y 16 bits, y utiliza el modelo Whisper base verificado. Úsala sólo con audio sintético, público o autorizado: la CLI imprime la transcripción completa. Una ejecución local no reemplaza el corpus español versionado ni la matriz de QA manual. Consulta [BENCHMARKING.md](docs/BENCHMARKING.md) para el comando, límites y criterios de interpretación.

## Documentación

- [Arquitectura](docs/ARCHITECTURE.md)
- [Privacidad](docs/PRIVACY.md)
- [Seguridad](docs/SECURITY.md)
- [Solución de problemas](docs/TROUBLESHOOTING.md)
- [Matriz de QA](docs/QA_CHECKLIST.md)
- [Plantilla de evidencia QA](docs/QA_EVIDENCE_TEMPLATE.md)
- [Benchmark de Whisper local](docs/BENCHMARKING.md)
- [Dependencias](docs/DEPENDENCIES.md)
- [Integración upstream futura](docs/UPSTREAM_INTEGRATION.md)
