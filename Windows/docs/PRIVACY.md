# Privacidad y flujo de datos

## LocalWhisper (predeterminado)

1. WASAPI captura la salida del sistema en memoria.
2. El audio se normaliza a WAV PCM mono, 16 kHz y 16 bits.
3. Whisper transcribe localmente.
4. Se envían a Gemini únicamente la transcripción y el contexto configurado para generar la respuesta.
5. Audio, transcripción y respuesta se liberan al finalizar la sesión.

## GeminiAudio (opt-in)

1. La aplicación exige un consentimiento versionado y revocable. Cada proceso comienza con Gemini Audio bloqueado: la aceptación guardada se conserva como historial, pero el usuario debe confirmar y guardar de nuevo la elección Gemini Audio para habilitar una subida en ese proceso.
2. El WAV se envía a Gemini sólo para transcripción; el contexto del proyecto no forma parte de esa petición.
3. La transcripción resultante y el contexto se envían en una petición textual separada para generar la respuesta.
4. No se cambia a este proveedor automáticamente cuando Whisper falla.

## Retención local

- No existe historial de sesiones.
- No se crean WAV temporales.
- Los diagnósticos excluyen API key, contexto, audio, transcripción y respuesta.
- La configuración no sensible y el consentimiento se guardan en JSON bajo `%LOCALAPPDATA%\MirrorPowerAI`.
- La API key y el contexto se cifran por separado con DPAPI para el usuario de Windows actual.

## Control del usuario

La configuración permite cambiar proveedor, retirar el consentimiento de nube, reemplazar o borrar la API key y cerrar la aplicación. El límite máximo de una captura es de 300 segundos.
