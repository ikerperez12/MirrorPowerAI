# MirrorPowerAI

App de barra de menús para macOS: te ayuda a responder preguntas que te hacen tus
compañeros durante una demo en videollamada, sin interrumpir la pantalla que estás
compartiendo.

## Cómo funciona

1. Pulsas **⌥⇧L** (Option+Shift+L) o el icono de la barra de menús para empezar a escuchar.
2. MirrorPowerAI captura el audio de salida del sistema (lo que se oye por los altavoces:
   la voz de tus compañeros en la videollamada) usando ScreenCaptureKit.
3. Vuelves a pulsar **⌥⇧L** para cortar. Se transcribe el audio (Speech framework de
   Apple) y se envía a Gemini junto con el contexto que le hayas dado sobre tu proyecto.
4. La respuesta aparece en un panel flotante.

## El panel flotante y la pantalla compartida

El panel usa `NSWindow.sharingType = .none`, una API estándar de AppKit que excluye
la ventana de cualquier captura de pantalla del sistema: Zoom, Meet, Teams, QuickTime,
OBS, etc. no la verán en ningún caso, aunque tengas toda la pantalla compartida.

**Límite honesto:** esto excluye la ventana de las *capturas por software*. No hay
(ni puede haber) forma de ocultarla de alguien que fotografíe o filme físicamente tu
monitor — eso queda fuera del alcance de cualquier aplicación.

## Requisitos y permisos

- macOS 14.2 o superior (usa Core Audio Process Taps para el audio del sistema).
- **Reconocimiento de voz**: se pide la primera vez que arranca la app.
- **Solo grabación del audio del sistema**: permiso independiente del de Grabación de
  pantalla (no hace falta este último). Se pedirá al iniciar la primera escucha;
  concédelo en Ajustes del Sistema → Privacidad y Seguridad, y vuelve a abrir la app.
- Una API key de Gemini (https://aistudio.google.com/apikey) — se guarda cifrada en el
  Keychain de macOS, nunca en texto plano ni en ningún archivo del proyecto. Configúrala
  desde el icono de la barra de menús → "Configurar Gemini API Key…".

## Compilar y ejecutar

```bash
./build.sh
open MirrorPowerAI.app
```

Primer uso:

1. Abre el icono de la barra de menús (forma de onda) → **Configurar Gemini API Key…**
2. Opcional pero recomendado: **Configurar contexto del sistema…** — pega una
   descripción de lo que vas a enseñar para que las respuestas sean más precisas.
3. Pulsa ⌥⇧L para empezar a escuchar, ⌥⇧L de nuevo para obtener la respuesta.

## Estructura del proyecto

- `Sources/MirrorPowerAI/AppDelegate.swift` — orquesta todo, menú de la barra de estado.
- `HotKeyManager.swift` — atajo global (Carbon HIToolbox, no requiere permiso de Accesibilidad).
- `AudioCaptureManager.swift` — captura de audio del sistema vía ScreenCaptureKit.
- `TranscriptionManager.swift` — transcripción en streaming con `SFSpeechRecognizer`.
- `GeminiClient.swift` — llamada a la API de Gemini (generateContent).
- `OverlayPanel.swift` — panel flotante excluido de la captura de pantalla.
- `KeychainHelper.swift` — almacenamiento seguro de la API key y el contexto.

## Limitaciones conocidas (v1)

- Solo capta el audio de **salida** del sistema (lo que oyes tú), no tu propio
  micrófono. Es lo que necesitas para preguntas que te hacen otros en una videollamada.
- El reconocimiento de voz usa el idioma `es-ES` por defecto (editable en
  `TranscriptionManager.swift`).
- Sin límite de longitud de escucha explícito, pero `SFSpeechRecognizer` funciona
  mejor con fragmentos cortos (unos pocos minutos como máximo).
- Modelo Gemini por defecto: `gemini-3.5-flash` (editable en `GeminiClient.swift`).
