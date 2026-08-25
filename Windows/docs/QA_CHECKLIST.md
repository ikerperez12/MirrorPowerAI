# Matriz de QA manual

Esta lista no se considera superada por el simple hecho de que compile. Marca cada caso sólo después de ejecutarlo en hardware real y conserva versión, fecha y evidencia. Usa una copia no versionada de [QA_EVIDENCE_TEMPLATE.md](QA_EVIDENCE_TEMPLATE.md) para registrar los resultados sin incluir datos sensibles.

## Shell y controles globales

- [ ] Preflight de runtime local: ejecutar `./Windows/verify-whisper-runtime.ps1`; comprueba que el portable carga Whisper CPU x64 sin modelo, audio, configuración, secretos ni red. `build.ps1` lo ejecuta también automáticamente después de publicar.
- [ ] Preflight de shell: con la aplicación normal cerrada, ejecutar `./Windows/verify-shell.ps1` en una sesión local e interactiva. Comprueba mutex, bandeja y el registro/liberación de `Alt+Shift+L`, sin tocar configuración, audio, modelos ni red.
- [ ] Preflight de UI: ejecutar `./Windows/verify-ui.ps1` en una sesión local e interactiva. Comprueba el ciclo WPF real de configuración y overlay con texto fijo no sensible, carga valores predeterminados desde una ruta temporal aislada, valida los estados Local/Gemini, renderizado, UI Automation, foco lógico inicial de configuración, foco del resultado y limpieza; no usa configuración del usuario, DPAPI, audio real, red, modelos ni sesiones, no deja la ruta temporal creada y no sustituye comprobar físicamente el foco de teclado, accesibilidad o captura.
- [ ] Abrir la aplicación normal y comprobar que Configuración aparece de forma visible, que cerrarla mantiene el icono de bandeja y que su menú se opera mediante teclado.
- [ ] Pulsar físicamente `Alt+Shift+L` para iniciar/detener una sesión y comprobar que al salir no queda icono ni proceso residual.
- [ ] Pulsar **Salir** inmediatamente después de **Guardar**: la aplicación espera el guardado; si supera 15 segundos permanece abierta, avisa sin datos sensibles y permite reintentar sin dejar configuración temporal ni cerrar dependencias en uso.
- [ ] Mientras **Guardar** está activo, los campos y botones quedan deshabilitados; **Cancelar**, la **X** y una nueva petición de Configuración desde la bandeja no ocultan ni recargan valores parciales. Al terminar, los controles vuelven a estar disponibles y el estado se anuncia.
- [ ] Durante una sesión, verificar que los cambios `Capturando`, `Procesando`, `Error` y regreso a `Preparado` ofrecen feedback de bandeja genérico y localizado, sin API key, contexto, audio, pregunta, respuesta ni detalle del proveedor; el arranque inicial en `Preparado` debe permanecer silencioso.

## Captura y exclusión del overlay

- [ ] Preflight de API: ejecutar `./Windows/build.ps1 -ReleaseGate` en una sesión local e interactiva; publica y verifica `WDA_EXCLUDEFROMCAPTURE`, shell, ciclo WPF/UI Automation y procedencia del portable. No sustituye las capturas reales de esta sección ni las pruebas manuales de accesibilidad.
- [ ] Snipping Tool: captura de pantalla completa.
- [ ] Snipping Tool: captura de ventana.
- [ ] OBS: Display Capture.
- [ ] OBS: Window Capture.
- [ ] Microsoft Teams: compartir pantalla y ventana.
- [ ] Google Meet en Chrome: compartir pantalla y ventana.
- [ ] Google Meet en Edge: compartir pantalla y ventana.
- [ ] Ante cualquier fallo de afinidad, no se renderiza texto sensible.

## Pantallas y DPI

- [ ] Pantalla integrada a 100 %, 150 % y 200 %.
- [ ] Monitor externo/DisplayLink a 100 %, 150 % y 200 %.
- [ ] Dos monitores con escalas distintas.
- [ ] Overlay dentro del área útil del monitor activo.
- [ ] Cambio de monitor durante el uso.

## Audio

- [ ] Preflight de WASAPI: ejecutar `./Windows/verify-wasapi.ps1` en una sesión local e interactiva; valida el ciclo nativo y la normalización sólo en memoria, pero no sustituye las pruebas reales de esta sección. Para verificar señal, reproducir audio autorizado y usar `-RequireAudibleSignal`.
- [ ] Altavoces integrados.
- [ ] Auriculares.
- [ ] **Todo el audio del sistema**: Teams de escritorio, Discord, Teams/Meet en Chrome y Teams/Meet en Edge; la conversación remota se captura desde el dispositivo elegido.
- [ ] **Una aplicación concreta**: con reproducción activa, **Actualizar** muestra Chrome, Edge y Discord sin exponer títulos de pestaña/reunión; cada selección captura sólo esa aplicación en los casos soportados.
- [ ] Cerrar o reiniciar la aplicación seleccionada produce un error visible y nunca cambia silenciosamente a la mezcla global.
- [ ] Teams de escritorio queda documentado y probado en modo global; un resultado silencioso en aislamiento por aplicación no se presenta como compatible.
- [ ] Cambio de dispositivo entre sesiones.
- [ ] Desconexión durante captura produce error recuperable.
- [ ] Audio vacío no se transcribe ni envía.
- [ ] Captura continua de cinco minutos termina sin archivos residuales.

## Transcripción y red

- [ ] Español con Whisper local offline.
- [ ] Inglés con Whisper local y detección automática.
- [ ] Gemini Audio exige consentimiento.
- [ ] Tras reiniciar la aplicación, Gemini Audio sigue bloqueado hasta que se confirma y guarda de nuevo el consentimiento.
- [ ] Retirar consentimiento impide nuevas subidas.
- [ ] Sin API key.
- [ ] Sin red.
- [ ] Cuota agotada/HTTP 429.
- [ ] No existe fallback silencioso a nube.

## Accesibilidad

- [ ] Toda la configuración se opera con Tab/Shift+Tab/Enter/Espacio/Escape.
- [ ] Foco visible y orden lógico.
- [ ] Narrator anuncia nombres, roles, valores, errores y cambios de estado.
- [ ] Narrator y la configuración de notificaciones de Windows permiten detectar el feedback genérico de bandeja durante una transición de sesión, sin revelar contenido sensible.
- [ ] Tema claro y oscuro.
- [ ] Alto contraste.
- [ ] Texto al 200 % sin pérdida de controles o contenido.
- [ ] Estados no dependen sólo del color.

## Rendimiento de referencia

- [ ] Corpus español local, no versionado y sin datos privados, con identificador, revisión, licencia y origen HTTPS documentados.
- [ ] Manifiesto de corpus JSON v1 cerrado: rutas WAV/TXT relativas y bajo el directorio del manifiesto, sin `.`/`..`, rutas absolutas, enlaces, junctions, reparse points, IDs o rutas normalizadas repetidos. La curación del corpus descarta además muestras repetidas por contenido o hard link.
- [ ] Cada WAV PCM mono de 16 kHz/16 bits y referencia UTF-8 está autorizado; sus SHA-256 minúsculas están en el manifiesto y el preflight completo pasa antes de resolver modelo o inferir.
- [ ] Preparar dependencias/restauración bloqueada antes de aislar la medición. Ejecutar exclusivamente la compuerta estable `./Windows/benchmark-corpus.ps1` con `-Model`, `-Language es` y `-Threads` explícitos: no ejecuta restore ni descarga el modelo. No redirigir ni conservar la salida de `--show-transcript`.
- [ ] Modelo Whisper base presente localmente y verificado antes de medir; el modo estable falla cerrado con activos ya preparados si tamaño o SHA-256 no coinciden. Su verificación queda registrada aparte del RTF.
- [ ] Evidencia conservada: commit, metadatos no sensibles y SHA-256 del manifiesto, modelo, idioma, hilos, duración total, WER y RTF agregados. El JSON no contiene rutas, IDs de elementos, audio, referencias, transcripciones ni hashes individuales.
- [ ] Whisper base: WER normalizado menor o igual al 20 %.
- [ ] Whisper base: factor de tiempo real menor o igual a 0,25 en Ryzen 7 7735HS.
- [ ] Si base falla precisión, evaluar `small` y exigir factor menor o igual a 0,5.
- [ ] Una ejecución local o sintética no sustituye el corpus español ni las demás pruebas manuales de esta matriz.
