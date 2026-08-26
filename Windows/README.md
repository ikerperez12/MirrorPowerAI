# MirrorPowerAI para Windows

Implementación nativa para Windows 11 x64, aislada de la aplicación macOS original. Esta rama es una entrega de código fuente para validación; no publica binarios ni modifica la línea Swift del proyecto.

## Estado

- Rama de desarrollo: `feat/windows-native`.
- Plataforma objetivo: Windows 11 24H2/25H2 x64.
- Runtime: WPF sobre .NET 10 LTS, SDK fijado en `10.0.400`.
- Audio: WASAPI loopback híbrido, con mezcla completa de un dispositivo o aislamiento de una aplicación compatible.
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

El script ejecuta preflight, restore bloqueado, build Release, xUnit, cobertura del núcleo, publicación autocontenida `win-x64` y una carga real del runtime Whisper CPU x64 sin modelo ni audio. La salida local queda en `Windows\artifacts\win-x64` y no se versiona; el equipo de destino no necesita instalar el runtime de .NET. Cada publicación genera `build-provenance.json` con commit, estado limpio/sucio, SDK, RID, hash SHA-256 del ejecutable e inventario ordenado de todos los ficheros del portable con un hash agregado. El manifiesto no se incluye en su propio inventario.

Para un candidato local que vaya a someterse a QA de distribución, usa la compuerta interactiva:

```powershell
.\Windows\build.ps1 -ReleaseGate
```

Además del flujo anterior, exige un árbol Git limpio, publica el ejecutable con su manifiesto de procedencia, verifica en una ventana WPF real que `WDA_EXCLUDEFROMCAPTURE` se puede aplicar y leer, y comprueba bandeja, mutex y `Alt+Shift+L` con recursos Windows reales. También ejecuta un ciclo WPF real y acotado de la configuración y del overlay protegido, incluidos el renderizado, los controles UI Automation, el foco del resultado y el cierre/limpieza. Finalmente verifica el inventario del portable. Requiere una sesión local e interactiva de Windows con DWM y la aplicación normal cerrada; se rechaza deliberadamente en CI y no sustituye las pruebas con Snipping Tool, OBS, Teams o Meet, ni la revisión manual de Narrator, alto contraste o DPI. No crea una release de GitHub ni autoriza redistribución de binarios.

Para comprobar después que una copia local del portable sigue siendo exactamente la que describe su manifiesto, sin usar red, audio, configuración ni secretos:

```powershell
.\Windows\verify-provenance.ps1
.\Windows\verify-whisper-runtime.ps1
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
.\Windows\verify-ui.ps1
.\Windows\verify-wasapi.ps1
.\Windows\benchmark-corpus.ps1 -?
```

`verify-shell.ps1` comprueba de forma aislada la bandeja, el mutex de instancia única y el registro/liberación de `Alt+Shift+L` usando recursos Windows reales. No crea configuración ni usa DPAPI; tampoco inicia audio, carga modelos ni realiza peticiones de red. Requiere una sesión local interactiva y una aplicación normal cerrada; no sustituye comprobar visualmente el icono ni pulsar físicamente el atajo durante la QA manual.

`verify-ui.ps1` ejecuta localmente un ciclo WPF real y acotado de la ventana de configuración y del overlay protegido con texto fijo no sensible. Carga valores predeterminados desde una ruta temporal aislada que no existe y usa almacén de secretos y catálogos de audio exclusivamente en memoria. Comprueba que los controles críticos se renderizan y exponen mediante UI Automation, que el foco lógico inicial llega a la API key, que se muestran correctamente los modos de audio global y por aplicación, que el consentimiento está oculto con Whisper local y visible con Gemini Audio, que el foco llega al resultado del overlay y que los dos ciclos se cierran y limpian. Omite el inicio normal: no usa DPAPI, audio real, red, modelos, sesiones ni configuración del usuario, y no deja la ruta temporal creada. Requiere una sesión local interactiva y se rechaza en CI. Es una comprobación de contrato y renderizado; no sustituye la prueba manual de foco de teclado, Narrator, alto contraste, DPI, capturas ni dispositivos reales.

`verify-whisper-runtime.ps1` inicia exclusivamente la ruta diagnóstica del portable y obliga a cargar el runtime CPU x64 fijado. Comprueba arquitectura, selección del runtime y una llamada nativa inocua; no abre ni descarga modelos, no procesa audio, no lee configuración o DPAPI y no usa red. Se ejecuta automáticamente tras cada publicación, también en CI.

`verify-wasapi.ps1` es una prueba diagnóstica explícita del dispositivo de salida predeterminado: captura tres segundos sólo en memoria, valida que el resultado sea WAV PCM mono de 16 kHz/16 bits y lo borra antes de salir. No usa red, modelo, configuración, API key ni crea archivos de audio. Sin opciones acepta silencio si llegaron muestras; si se está reproduciendo audio autorizado y se quiere exigir señal real, ejecuta `./Windows/verify-wasapi.ps1 -RequireAudibleSignal`. No se invoca desde CI ni desde `-ReleaseGate`, para no iniciar una captura sin una intención explícita del operador.

## Primera ejecución

1. Abre `MirrorPowerAI.Windows.exe` desde la carpeta publicada. **Configuración** aparece automáticamente; al cerrarla la aplicación permanece en la bandeja.
2. Introduce la API key de Gemini. Se protege con DPAPI para el usuario actual.
3. En **Fuente de audio**, elige:
   - **Todo el audio del sistema (recomendado para reuniones)**: captura lo que oyes en el dispositivo elegido. Es el modo recomendado para Teams de escritorio y el más robusto para Teams, Discord, Meet, Zoom y audio del navegador.
   - **Una aplicación concreta**: inicia antes la reunión o reproducción, pulsa **Actualizar** y selecciona la aplicación. Windows captura el árbol de ese proceso sin mezclar las demás aplicaciones.
4. Revisa el contexto del proyecto, idioma y dispositivo de salida si elegiste el audio completo del sistema.
5. Mantén **Whisper local** para máxima privacidad o selecciona **Gemini Audio**, acepta el consentimiento de nube y guarda la configuración. Por privacidad, Gemini Audio vuelve a quedar bloqueado al reiniciar la aplicación hasta que confirmes y guardes esa elección de nuevo.
6. Reproduce audio o entra en la reunión y pulsa `Alt+Shift+L`: aparecerá un indicador protegido sin quitar el foco a la reunión. Mientras la fuente entregue silencio dirá **aún no se detecta audio**; cuando una muestra supere el mismo umbral usado para validar la grabación cambiará a **audio detectado correctamente**. Vuelve a pulsarlo para detener; el mismo indicador mostrará **Procesando…** hasta que aparezca la respuesta o un error accionable. Si nunca cambia a audio detectado, actualiza o corrige la fuente antes de procesar.

No existe fallback silencioso entre fuentes: si seleccionas una aplicación y deja de estar disponible, la sesión falla de forma visible y debes actualizar la lista o elegir conscientemente el audio completo del sistema.

Antes de abrir el dispositivo de audio, MirrorPowerAI comprueba que exista una clave Gemini estructuralmente válida. Si falta, muestra el error en el panel protegido y no inicia una grabación que no podría producir respuesta.

La primera transcripción local descarga el modelo fijado:

- Archivo: `ggml-base.bin`
- Tamaño: `147951465` bytes
- SHA-256: `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe`
- Revisión: `5359861c739e955e79d9a303bcbc70fb988958b1`

El modelo sólo se activa después de verificar tamaño y hash. Una descarga incompleta o manipulada se descarta.

## Privacidad

- `LocalWhisper`: el audio y la transcripción permanecen en el equipo; únicamente la pregunta transcrita y el contexto se envían a Gemini para producir la respuesta.
- `GeminiAudio`: el WAV capturado se envía a Gemini para transcribirlo sólo tras un consentimiento explícito guardado correctamente en el proceso actual; después se realiza la misma petición textual de respuesta. El consentimiento guardado conserva el historial de aceptación, pero no reactiva la subida tras reiniciar la aplicación.
- No existe fallback automático de local a nube.
- Audio, transcripciones y respuestas viven en memoria y no se guardan como historial.
- La API key se protege con DPAPI `CurrentUser`; la configuración no sensible se guarda en `%LOCALAPPDATA%\MirrorPowerAI`.
- No hay telemetría.

Consulta [PRIVACY.md](docs/PRIVACY.md) para el flujo de datos completo.

## Límites conocidos

- No captura micrófono. Captura audio renderizado por Windows, ya sea la mezcla del dispositivo o el árbol de una aplicación.
- El aislamiento por aplicación requiere Windows build 20348 o posterior y depende de cómo la aplicación publique sus sesiones de audio. La API oficial funciona con procesos de navegador y muchas aplicaciones, pero Microsoft tiene documentado que Teams de escritorio puede entregar silencio en este modo; usa **Todo el audio del sistema** para Teams.
- Una aplicación sólo aparece en el selector mientras está ejecutándose y mantiene una sesión de audio. Inicia la reunión o reproduce audio y pulsa **Actualizar**.
- WASAPI puede omitir audio protegido por DRM.
- `WDA_EXCLUDEFROMCAPTURE` protege frente a APIs públicas que respetan DWM; no protege frente a una cámara física, drivers o software que eluda ese mecanismo.
- Windows 10, ARM64, MSIX, firma de código, autoactualización y publicación de releases quedan fuera de esta primera versión.
- La etiqueta estable requiere completar la matriz manual de [QA_CHECKLIST.md](docs/QA_CHECKLIST.md).
- Los avisos de bandeja para cambios de estado son genéricos y localizados (`Capturando`, `Procesando`, `Error` o `Preparado`). El estado inicial `Preparado` no genera aviso; estos avisos nunca incluyen API key, contexto, audio, pregunta, respuesta ni detalle del proveedor.
- El indicador de captura/procesamiento se protege con la misma afinidad que la respuesta y no se activa, por lo que no debe robar el foco de Teams, Discord o el navegador. Distingue de forma local entre silencio y señal detectada sin mostrar niveles, conservar muestras adicionales ni emitir telemetría. Si Windows no confirma la exclusión de captura, el indicador falla de forma segura y permanece el feedback de bandeja.

## Benchmark de Whisper local

La utilidad individual `MirrorPowerAI.Benchmark` mide WER y RTF con WAV PCM mono de 16 kHz y 16 bits. Su salida normal oculta la ruta y la transcripción; revelar esta última requiere `--show-transcript` o `-ShowTranscript` explícito y no sirve como evidencia estable.

La evidencia de rendimiento estable se obtiene exclusivamente con `Windows\benchmark-corpus.ps1` y un manifiesto local v1 no versionado. El wrapper exige español, modelo `base` o `small` fijado, hilos explícitos y un modelo local cuyo tamaño/SHA-256 se hayan verificado; no ejecuta restore ni descarga el modelo, y falla cerrada si los activos ya preparados faltan. Sólo emite un JSON agregado tras completar todo el corpus, sin rutas, IDs de elementos, referencias, transcripciones ni hashes individuales. Una ejecución de corpus no sustituye la matriz manual de audio, captura, DPI, accesibilidad y red. Consulta [BENCHMARKING.md](docs/BENCHMARKING.md) para preparar dependencias fuera de la medición, el formato, comando, límites y criterios de interpretación.

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
