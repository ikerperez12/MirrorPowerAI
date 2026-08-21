# Benchmark de Whisper local

`MirrorPowerAI.Benchmark` tiene dos modos separados. El modo de un único WAV sirve para diagnóstico autorizado; el modo de corpus es la única evidencia aceptable para la puerta de rendimiento estable. Ambos usan exclusivamente los descriptores Whisper fijados en código (`base` o `small`), con tamaño y SHA-256 verificados.

No se ha descargado ni ejecutado un corpus o modelo como parte de esta implementación. La persona que hace QA debe elegir un corpus público o autorizado y conservarlo fuera del repositorio.

## Privacidad y límites

Usa sólo audio sintético, público o con permiso explícito. No añadas WAV, referencias, manifiestos de corpus ni resultados con contenido a Git. Guarda el corpus en una carpeta local independiente o bajo `Windows\artifacts\`, que está ignorada.

El modo individual no muestra por defecto ni la ruta del WAV ni la transcripción. Sólo `--show-transcript` en la CLI o `-ShowTranscript` en `Windows\benchmark.ps1` revela el texto; esa acción es opt-in y no es válida para evidencia estable.

El modo corpus nunca incluye en su JSON o resumen de consola rutas locales, IDs de elementos, texto de audio, referencias, transcripciones ni hashes individuales de WAV/TXT. Sólo publica metadatos no sensibles del corpus, el SHA-256 del manifiesto completo y métricas agregadas.

## Diagnóstico de un WAV

El WAV debe ser RIFF PCM canónico, mono, 16 kHz y 16 bits, de como máximo cinco minutos. La referencia opcional debe ser UTF-8, no superar 1 MiB y contener una palabra tras normalizarse.

```powershell
.\Windows\benchmark.ps1 --audio <wav-autorizado> `
  --reference-file <referencia-autorizada.txt> `
  --language es
```

`--model <base|small>` limita el modelo a los dos artefactos fijados. `--threads <1-32>` configura la inferencia y `--model-dir <ruta>` cambia la caché. En este modo `base`, `auto` y la mitad de CPU (máximo ocho hilos) son valores predeterminados para conservar compatibilidad de diagnóstico.

## Corpus local v1

La compuerta estable requiere un manifiesto JSON **local y no versionado** con este esquema cerrado. No se admiten propiedades adicionales ni duplicadas.

```json
{
  "version": 1,
  "id": "qa-spanish",
  "revision": "2026.08",
  "license": "CC0-1.0",
  "source": "https://example.org/dataset",
  "items": [
    {
      "id": "item-0001",
      "audio": "audio/item-0001.wav",
      "audioSha256": "<sha-256-minusculas-de-64-caracteres>",
      "reference": "reference/item-0001.txt",
      "referenceSha256": "<sha-256-minusculas-de-64-caracteres>"
    }
  ]
}
```

`id`, `revision` y `license` son metadatos cortos no sensibles; `source` debe ser una URL HTTPS sin credenciales, consulta ni fragmento. Los IDs de elemento son sólo internos y no salen en la evidencia. Cada ruta `audio`/`reference` debe ser relativa al directorio del manifiesto, no puede contener `.` o `..`, una letra de unidad, UNC, enlace simbólico, junction ni otro reparse point. Se comprueba cada componente y, antes de interpretar el RIFF/WAV o la referencia, su ruta final Windows desde el handle debe seguir bajo ese directorio; esto es una defensa de integridad local, no una frontera frente a un administrador o kernel hostil. Los WAV deben acabar en `.wav`; las referencias en `.txt`. No se permite repetir un ID ni una ruta normalizada; la curación del corpus debe además excluir muestras duplicadas por contenido o hard link.

El manifiesto queda limitado a 1 MiB y 10 000 elementos; cada referencia a 1 MiB. Antes de resolver el modelo o inferir, el programa abre y valida todo el corpus. Justo antes de cada inferencia vuelve a abrir el WAV y la referencia, verifica sus SHA-256 desde el mismo handle y vuelve a validar el WAV. Por tanto, una referencia ausente, demasiado grande, modificada, mal codificada o un hash incorrecto bloquea toda la ejecución sin generar un resultado parcial.

## Ejecutar la puerta estable

El envoltorio `Windows\benchmark-corpus.ps1` es intencionadamente estable: exige `-Language es`, añade `--stable` y exige un modelo local verificado. No invoca `dotnet restore`, descarga de modelo ni un cliente HTTP propio: compila sólo con `--no-restore`, ejecuta con `--no-build --no-restore` y desactiva el aviso de cargas de trabajo del SDK. Si faltan los activos ya restaurados o el modelo `ggml-base.bin`/`ggml-small.bin` no pasa tamaño/SHA-256, falla cerrada.

Prepara el checkout **antes** de entrar en la sesión de medición, por ejemplo con `./Windows/build.ps1 -SkipPublish`; ese paso de preparación usa restore bloqueado y puede necesitar una caché NuGet o conectividad. No forma parte de la medición estable. Después puedes desconectar la red o aplicar la política de red de QA y ejecutar únicamente el wrapper de corpus.

```powershell
.\Windows\benchmark-corpus.ps1 `
  -Manifest <manifiesto-local-no-versionado.json> `
  -OutputJson <resultado-agregado-seguro.json> `
  -Model base `
  -Language es `
  -Threads 8
```

Para evaluar `small` después de que `base` falle el objetivo de precisión, repite el mismo corpus con `-Model small`. No cambies modelo, idioma, número de hilos, manifiesto ni caché durante una comparación.

El wrapper no reproduce su línea de comandos, las rutas de manifiesto ni la ruta de salida cuando hay un fallo. En éxito sólo confirma la terminación; el JSON agregado es la evidencia durable. El resultado se genera en un temporal del mismo directorio y sólo reemplaza el JSON de destino después de que **todos** los elementos hayan terminado. Si falla preflight, modelo, hash, WAV, referencia, cancelación o inferencia, no hay resumen de éxito ni JSON parcial. El directorio de salida debe ser local y no estar controlado por otro proceso: la comprobación de reparse points no convierte un reemplazo concurrente de directorio en una frontera de seguridad.

## Evidencia segura y fórmulas

El JSON determinista v1 y el resumen contienen:

- `id`, `revision`, `license`, `source` y `manifestSha256` del corpus;
- modelo fijado, idioma, hilos y si el modo es estable;
- número de elementos, duración total, tiempo de verificación de modelo y tiempo total de Whisper;
- ediciones, palabras de referencia, WER y RTF agregados.

No incorpora fecha, rutas, IDs de elementos, audio, referencia, transcripción ni hashes por elemento. El SHA-256 del manifiesto se calcula sobre la misma lectura de bytes que se decodifica y analiza; identifica exactamente la estructura evaluada.

Las fórmulas no promedian resultados por elemento:

- **WER** = `suma de ediciones / suma de palabras de referencia`.
- **RTF** = `suma de tiempo de Whisper / suma de duración de audio`.

La preparación y verificación del modelo se informa por separado y no entra en RTF. `base` debe alcanzar WER normalizado menor o igual que 20 % y RTF menor o igual que 0,25 en el Ryzen 7 7735HS de referencia. Si falla precisión, `small` debe cumplir RTF menor o igual que 0,5.

Conserva el JSON agregado y una copia local no sensible de [QA_EVIDENCE_TEMPLATE.md](QA_EVIDENCE_TEMPLATE.md). El corpus no sustituye las pruebas físicas de WASAPI, capturadores, DPI, accesibilidad, consentimiento ni red de [QA_CHECKLIST.md](QA_CHECKLIST.md).
