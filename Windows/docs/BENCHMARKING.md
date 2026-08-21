# Benchmark de Whisper local

`MirrorPowerAI.Benchmark` mide la transcripción local con modelos Whisper fijados por origen, revisión, tamaño y SHA-256. Usa `base` por defecto y permite evaluar `small` sólo como alternativa de benchmark si `base` no alcanza el objetivo de precisión. La aplicación sigue usando exclusivamente `base` hasta que el corpus autorizado demuestre que conviene cambiarlo. La utilidad valida el WAV de entrada, prepara el modelo elegido cuya integridad se comprueba y muestra el tiempo de Whisper, el RTF y, si se proporciona una referencia, el WER normalizado.

## Uso seguro

Ejecuta la herramienta únicamente con un WAV sintético, público o para el que tengas autorización. La CLI imprime la transcripción completa en la salida estándar, además de la ruta del audio; no le pases audio privado, sensible o confidencial a una consola cuyo registro pueda conservarse o compartirse. La inferencia de Whisper se ejecuta localmente, aunque la primera preparación del modelo puede requerir su descarga desde el origen fijado.

El archivo de audio debe ser un WAV RIFF PCM canónico, mono, a 16 kHz y 16 bits. La herramienta rechaza otros formatos, WAV mal formados y audio de más de cinco minutos. Si se usa un archivo de referencia, debe ser UTF-8, no superar 1 MiB y contener al menos una palabra después de normalizarse.

## Ejecutar una medición local

Desde la raíz del repositorio, con .NET SDK 10.0.400 disponible, sustituye las rutas de ejemplo por un WAV y una referencia autorizados:

```powershell
.\Windows\benchmark.ps1 --audio .\ruta\audio-publico.wav `
  --reference-file .\ruta\audio-publico.txt `
  --language es
```

Para inspeccionar las opciones sin ejecutar una transcripción:

```powershell
.\Windows\benchmark.ps1 --help
```

Opciones principales:

- `--audio <ruta>` es obligatorio.
- `--reference <texto>` o `--reference-file <ruta>` permiten calcular WER; son excluyentes.
- `--language <código|auto>` usa `auto` por defecto.
- `--model <base|small>` selecciona únicamente uno de los dos descriptores verificables; el predeterminado es `base`. No acepta rutas, URL ni identificadores de modelo libres.
- `--threads <1-32>` fija los hilos de inferencia; si se omite, usa la mitad de los procesadores lógicos, con un máximo de 8.
- `--model-dir <ruta>` cambia el directorio del modelo; el predeterminado es `%LOCALAPPDATA%\MirrorPowerAI\models`.

La herramienta prepara el descriptor fijo correspondiente y sólo activa un archivo cuyo tamaño y SHA-256 coinciden con él. El resultado conserva el alias, archivo y SHA-256 para hacer auditable la evidencia. La preparación o descarga del modelo se informa por separado y no forma parte del RTF.

## Interpretar el resultado

- **Whisper (carga + inferencia)** incluye la carga local y la inferencia del modelo.
- **RTF** es `tiempo de Whisper / duración del audio`; por ejemplo, `0,25x` implica que esa fase tarda una cuarta parte de la duración del audio. No incluye la preparación ni la verificación del modelo.
- **WER normalizado** es `ediciones / palabras de referencia`. Para la comparación se ignoran mayúsculas, acentos y puntuación. Sin referencia no se muestra WER.

Para una comparación repetible, registra el comando, la versión del código, el modelo verificado, idioma, número de hilos, duración del audio, WER y RTF. La referencia de rendimiento actual se evalúa con Whisper base en un Ryzen 7 7735HS: WER normalizado menor o igual a 20 % y RTF menor o igual a 0,25. Si `base` no cumple precisión, evalúa el descriptor fijo `small` con un comando explícito como el siguiente y exige RTF menor o igual a 0,5:

```powershell
.\Windows\benchmark.ps1 --audio .\ruta\audio-publico.wav `
  --reference-file .\ruta\audio-publico.txt `
  --language es `
  --model small
```

Los dos descriptores están cerrados en código: no se acepta un modelo de procedencia no verificada.

## Alcance de la medición

Una prueba local o sintética comprueba que la ruta de benchmark funciona, pero no sustituye al corpus español versionado y sin datos privados requerido por la matriz de QA. Tampoco sustituye las comprobaciones manuales de audio, consentimiento, red, accesibilidad, captura y pantallas. Completa y conserva la evidencia de [QA_CHECKLIST.md](QA_CHECKLIST.md) antes de considerar una etiqueta estable.
