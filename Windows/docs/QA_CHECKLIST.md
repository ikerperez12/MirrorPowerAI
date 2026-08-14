# Matriz de QA manual

Esta lista no se considera superada por el simple hecho de que compile. Marca cada caso sólo después de ejecutarlo en hardware real y conserva versión, fecha y evidencia.

## Captura y exclusión del overlay

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

- [ ] Altavoces integrados.
- [ ] Auriculares.
- [ ] Cambio de dispositivo entre sesiones.
- [ ] Desconexión durante captura produce error recuperable.
- [ ] Audio vacío no se transcribe ni envía.
- [ ] Captura continua de cinco minutos termina sin archivos residuales.

## Transcripción y red

- [ ] Español con Whisper local offline.
- [ ] Inglés con Whisper local y detección automática.
- [ ] Gemini Audio exige consentimiento.
- [ ] Retirar consentimiento impide nuevas subidas.
- [ ] Sin API key.
- [ ] Sin red.
- [ ] Cuota agotada/HTTP 429.
- [ ] No existe fallback silencioso a nube.

## Accesibilidad

- [ ] Toda la configuración se opera con Tab/Shift+Tab/Enter/Espacio/Escape.
- [ ] Foco visible y orden lógico.
- [ ] Narrator anuncia nombres, roles, valores, errores y cambios de estado.
- [ ] Tema claro y oscuro.
- [ ] Alto contraste.
- [ ] Texto al 200 % sin pérdida de controles o contenido.
- [ ] Estados no dependen sólo del color.

## Rendimiento de referencia

- [ ] Corpus español versionado y sin datos privados.
- [ ] Whisper base: WER normalizado menor o igual al 20 %.
- [ ] Whisper base: factor de tiempo real menor o igual a 0,25 en Ryzen 7 7735HS.
- [ ] Si base falla precisión, evaluar `small` y exigir factor menor o igual a 0,5.
