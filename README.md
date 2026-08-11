# MirrorPowerAI

App de barra de menús para macOS: te ayuda a responder preguntas que te hacen tus
compañeros durante una demo en videollamada, sin interrumpir la pantalla que estás
compartiendo.

## Cómo funciona

1. Pulsas **⌥⇧L** (Option+Shift+L) o el icono de la barra de menús para empezar a escuchar.
2. MirrorPowerAI captura el audio de salida del sistema (lo que se oye por los altavoces:
   la voz de tus compañeros en la videollamada) usando Core Audio Process Taps.
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

## Instalación (cada persona compila su propia copia)

No hay binario para descargar — cada usuario compila MirrorPowerAI en su propio Mac.
Esto es intencional: una app compilada localmente no lleva el atributo de cuarentena
que macOS pone a lo descargado de internet, así que Gatekeeper no la bloquea con el
aviso de "desarrollador no identificado" que sí verías con un `.app` ya compilado.

**Requisitos previos:**

- macOS 14.2 o superior.
- Xcode Command Line Tools (gratis, no hace falta instalar Xcode completo ni tener
  cuenta de desarrollador): `xcode-select --install`.
- Una API key de Gemini propia y gratuita: https://aistudio.google.com/apikey

**Pasos:**

```bash
git clone https://github.com/RafaTatay/MirrorPowerAI.git
cd MirrorPowerAI
./build.sh
open MirrorPowerAI.app
```

Primer uso:

1. macOS pedirá permiso de **Reconocimiento de voz** al arrancar, y de **Solo
   grabación del audio del sistema** al pulsar ⌥⇧L por primera vez — concede ambos
   (puede pedir que reabras la app tras conceder el segundo).
2. Abre el icono de la barra de menús (forma de onda) → **Configurar Gemini API
   Key…** y pega tu propia key. Se guarda cifrada en tu Keychain — nunca se comparte
   ni sale de tu máquina.
3. Opcional pero recomendado: **Configurar contexto del sistema…** — pega una
   descripción de lo que vas a enseñar para que las respuestas sean más precisas.
4. Pulsa ⌥⇧L para empezar a escuchar, ⌥⇧L de nuevo para obtener la respuesta.

## Estructura del proyecto

- `Sources/MirrorPowerAI/AppDelegate.swift` — orquesta todo, menú de la barra de estado.
- `HotKeyManager.swift` — atajo global (Carbon HIToolbox, no requiere permiso de Accesibilidad).
- `AudioCaptureManager.swift` — captura de audio del sistema vía Core Audio Process Taps.
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
