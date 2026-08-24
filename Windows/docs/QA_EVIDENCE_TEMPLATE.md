# Registro de evidencia de QA Windows

Haz una copia local de esta plantilla dentro de `Windows\artifacts\qa-evidence\` para cada candidato. Esa carpeta no se versiona. No pegues API keys, contexto de proyecto, audio, transcripciones, respuestas, nombres de reuniones ni capturas que contengan información sensible.

## Identificación del candidato

- Fecha y zona horaria:
- Commit Git completo:
- SHA-256 de `MirrorPowerAI.Windows.exe` publicado:
- Copia de `build-provenance.json` o ruta local no sensible:
- Windows: edición, compilación y arquitectura:
- .NET SDK usado:
- VC++ Redistributable x64:
- Equipo: CPU, RAM y GPU (si aplica):
- Dispositivos de salida de audio:
- Monitores, conexión y escalado DPI:

## Preflight automatizable local

| Comprobación | Comando | Resultado | Evidencia local no sensible |
|---|---|---|---|
| Build y WDA | `./Windows/build.ps1 -ReleaseGate` | Pendiente | |
| Inventario del portable | `./Windows/verify-provenance.ps1` | Pendiente | |
| Bandeja, mutex y hotkey | `./Windows/verify-shell.ps1` | Pendiente | |
| Ciclo WPF, UI Automation y limpieza | `./Windows/verify-ui.ps1` | Pendiente | |
| WASAPI con muestras | `./Windows/verify-wasapi.ps1` | Pendiente | |
| WASAPI con señal autorizada | `./Windows/verify-wasapi.ps1 -RequireAudibleSignal` | Pendiente / no aplicable | |
| Suite xUnit y cobertura | `./Windows/test.ps1` | Pendiente | |

La verificación WASAPI no guarda audio ni muestra contenido, pero se ejecuta sólo con la intención expresa del operador. Si se exige señal, reproduce únicamente audio autorizado y no protegido por DRM. `verify-ui.ps1` usa texto fijo no sensible, valores predeterminados desde una ruta temporal aislada y adaptadores en memoria; no usa configuración del usuario, DPAPI, audio real, red, modelos o sesiones y no deja la ruta temporal creada. Su resultado automatizable no sustituye Narrator, alto contraste, DPI o la matriz de captura real.

## Captura y exclusión del overlay

Registra una captura de prueba sin pregunta, respuesta ni texto de usuario. El resultado esperado es que el overlay esté ausente de la captura, mientras la aplicación notifica y no muestra contenido sensible si la protección no puede verificarse.

| Caso | Monitor / DPI | Resultado | Ruta local de evidencia segura | Observaciones |
|---|---|---|---|---|
| Snipping Tool: pantalla completa | | Pendiente | | |
| Snipping Tool: ventana | | Pendiente | | |
| OBS: Display Capture | | Pendiente | | |
| OBS: Window Capture | | Pendiente | | |
| Teams: compartir pantalla | | Pendiente | | |
| Teams: compartir ventana | | Pendiente | | |
| Meet Chrome: pantalla | | Pendiente | | |
| Meet Chrome: ventana | | Pendiente | | |
| Meet Edge: pantalla | | Pendiente | | |
| Meet Edge: ventana | | Pendiente | | |

## Pantallas, DPI y audio

| Caso | Entorno | Resultado | Evidencia local no sensible | Observaciones |
|---|---|---|---|---|
| Panel integrado 100/150/200 % | | Pendiente | | |
| Externo o DisplayLink 100/150/200 % | | Pendiente | | |
| Dos monitores con escalas distintas | | Pendiente | | |
| Cambio de monitor durante uso | | Pendiente | | |
| Altavoces integrados | | Pendiente | | |
| Auriculares | | Pendiente | | |
| Cambio de dispositivo entre sesiones | | Pendiente | | |
| Desconexión durante captura | | Pendiente | | |
| Cinco minutos sin residuo de audio | | Pendiente | | |

## Privacidad, transcripción y red

No conserves audio, transcripciones ni respuestas como evidencia. Registra sólo el resultado categorizado.

| Caso | Proveedor / red | Resultado | Categoría de error o éxito | Observaciones |
|---|---|---|---|---|
| Whisper local español offline | Local | Pendiente | | |
| Whisper local inglés / auto | Local | Pendiente | | |
| Audio vacío no se procesa | Local | Pendiente | | |
| Gemini Audio pide consentimiento | Gemini | Pendiente | | |
| Retirar consentimiento bloquea nuevas subidas | Gemini | Pendiente | | |
| Sin API key | Gemini | Pendiente | | |
| Sin red | Ambos | Pendiente | | |
| HTTP 429 / cuota | Gemini | Pendiente | | |
| Sin fallback local-a-nube | Ambos | Pendiente | | |

## Accesibilidad

| Caso | Idioma / tema | Resultado | Observaciones |
|---|---|---|---|
| Teclado completo | | Pendiente | |
| Foco visible y orden lógico | | Pendiente | |
| Narrator: nombres, roles, valores y errores | | Pendiente | |
| Narrator y feedback de bandeja genérico, sin contenido sensible | | Pendiente | |
| Tema claro y oscuro | | Pendiente | |
| Alto contraste | | Pendiente | |
| Texto al 200 % | | Pendiente | |
| Estados no dependen sólo del color | | Pendiente | |

## Benchmark autorizado

- Identificador del corpus español (no sensible):
- Revisión del corpus:
- Licencia/permiso del corpus:
- Origen HTTPS del corpus (sin token, consulta ni fragmento):
- SHA-256 del manifiesto JSON v1:
- Commit Git:
- CPU y número de hilos:
- Dependencias restauradas/compiladas antes de aislar la medición (sí/no):
- Política de red aplicada durante la medición (desconectada, firewall u otra):
- Modo estable con modelo cacheado verificado (sí/no):
- Comando exacto sin rutas privadas (usar marcadores `<manifiesto>` y `<resultado>`):
- Resultado JSON agregado seguro conservado localmente (sí/no):
- Modelo `base`: WER / RTF:
- ¿Cumple WER <= 20 % y RTF <= 0,25?:
- Si no cumple precisión, modelo `small`: WER / RTF:
- ¿Cumple RTF <= 0,5?:

No copies rutas locales, IDs de elementos, WAV, referencias, transcripciones, hashes por elemento ni texto de `--show-transcript`. La evidencia válida es el resumen/JSON agregado de `Windows\benchmark-corpus.ps1`; ese wrapper exige español, hilos/modelo explícitos, no ejecuta restore ni descarga modelo, y requiere activos preparados previamente.

## Resultado de la ejecución

- [ ] Toda fila requerida de `QA_CHECKLIST.md` tiene evidencia.
- [ ] No se ha incluido contenido sensible en este registro ni en el repositorio.
- [ ] Los fallos se han reproducido, clasificado y resuelto o documentado como limitación.
- [ ] El candidato puede avanzar a revisión de licencia y decisión del autor.

Firma o responsable de QA:
