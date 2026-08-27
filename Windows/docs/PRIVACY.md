# Privacidad y flujo de datos

## LocalWhisper (predeterminado)

1. WASAPI captura en memoria la fuente elegida: la mezcla de un dispositivo de salida o el árbol de una aplicación compatible.
2. El audio se normaliza a WAV PCM mono, 16 kHz y 16 bits.
3. Whisper transcribe localmente.
4. Cuando se detecta una pregunta, se envían a Gemini la pregunta, el contexto configurado y un contexto rodante acotado de las transcripciones recientes para resolver referencias conversacionales. El audio local nunca forma parte de esta petición.
5. Cada segmento de audio se libera después de transcribirlo. El contexto rodante y la última respuesta viven sólo en memoria y se eliminan al terminar la escucha o cerrar la aplicación.

## GeminiAudio (opt-in)

1. La aplicación exige un consentimiento versionado y revocable. Cada proceso comienza con Gemini Audio bloqueado: la aceptación guardada se conserva como historial, pero el usuario debe confirmar y guardar de nuevo la elección Gemini Audio para habilitar una subida en ese proceso.
2. El WAV se envía a Gemini sólo para transcripción; el contexto del proyecto no forma parte de esa petición.
3. La transcripción resultante, el contexto configurado y el contexto rodante acotado se envían en una petición textual separada para generar la respuesta.
4. No se cambia a este proveedor automáticamente cuando Whisper falla.

## Retención local

- No existe historial de sesiones.
- No se crean WAV temporales.
- Los diagnósticos excluyen API key, contexto, audio, transcripción y respuesta.
- La configuración no sensible y el consentimiento se guardan en JSON bajo `%LOCALAPPDATA%\MirrorPowerAI`.
- La API key y el contexto se cifran por separado con DPAPI para el usuario de Windows actual.

## Control del usuario

La configuración permite elegir conscientemente entre audio completo del sistema y una aplicación, cambiar proveedor, retirar el consentimiento de nube, reemplazar o borrar la API key y cerrar la aplicación. No existe fallback silencioso de aplicación a sistema ni de transcripción local a nube. El selector sólo conserva nombre de ejecutable y PID; nunca almacena títulos de reuniones, ventanas o pestañas. La escucha continua está acotada a ocho horas por sesión nativa y mantiene sólo segmentos breves y un contexto textual rodante en memoria.
