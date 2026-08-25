# Solución de problemas

## El preflight no encuentra .NET 10.0.400

Ejecuta:

```powershell
.\Windows\install-dotnet.ps1
```

La instalación se realiza en `%USERPROFILE%\.dotnet` y no necesita Developer Mode.

## Falta Visual C++ Redistributable

Whisper local requiere Microsoft Visual C++ Redistributable 2015-2022 x64. Instala el paquete oficial de Microsoft y vuelve a ejecutar `Windows\preflight.ps1`.

## La aplicación no puede inicializar sus componentes de Windows

Cierra cualquier instancia de MirrorPowerAI, confirma en el Administrador de tareas que el proceso terminó y vuelve a abrir el portable completo. Si el mensaje reaparece, ejecuta `Windows\build.ps1 -ReleaseGate` desde el checkout: la aplicación libera los componentes creados parcialmente y no muestra ni registra detalles que puedan contener rutas o configuración del usuario.

## El runtime Whisper CPU x64 no carga

Ejecuta `Windows\verify-whisper-runtime.ps1`. Si falla, confirma que estás usando el portable completo y no sólo el `.exe`, que la carpeta `runtimes\win-x64` conserva sus cuatro DLL y que el Visual C++ Redistributable x64 pasa `Windows\preflight.ps1`. No copies DLL desde otra versión ni desde una arquitectura diferente; vuelve a publicar con `Windows\build.ps1` para restaurar el inventario fijado.

## El hotkey no funciona

Otra aplicación puede haber registrado `Alt+Shift+L`. Cierra la aplicación en conflicto y reinicia MirrorPowerAI. El registro usa `MOD_NOREPEAT` para evitar repeticiones mientras se mantienen las teclas.

## No se captura audio

- Para Teams de escritorio usa **Todo el audio del sistema**; el aislamiento por aplicación puede devolver silencio por una limitación conocida de la API de Windows.
- En modo **Todo el audio del sistema**, comprueba que existe reproducción audible en el dispositivo seleccionado y evita cambiarlo o desconectarlo durante una captura.
- En modo **Una aplicación concreta**, inicia la reunión o reproducción, abre Configuración, pulsa **Actualizar** y vuelve a seleccionar la aplicación. Si se cierra o reinicia durante la captura, detén la sesión y actualiza la lista.
- En navegador selecciona el ejecutable correspondiente (`msedge`, `chrome`, `firefox`, etc.), no el título de la pestaña. Si la arquitectura multiproceso del navegador o de la aplicación no entrega señal, usa conscientemente **Todo el audio del sistema**.
- El contenido protegido por DRM puede no estar disponible mediante WASAPI loopback.
- MirrorPowerAI nunca amplía silenciosamente una captura por aplicación al audio global.

## La aplicación parece no abrirse

La configuración aparece automáticamente al iniciar. Si la cierras, MirrorPowerAI sigue activo en la bandeja de Windows; desde su icono puedes volver a abrir **Configuración** o salir. Si Windows ocultó el icono dentro del menú de iconos desbordados, abre ese menú. Sólo puede ejecutarse una instancia a la vez.

## Whisper no descarga o rechaza el modelo

La aplicación descarta cualquier archivo cuyo tamaño o SHA-256 no coincidan con la versión fijada. Revisa conectividad, espacio libre y software de seguridad; después vuelve a intentarlo. No renombres manualmente una descarga parcial.

## Gemini devuelve 401, 429 o timeout

- `401`: revisa la API key.
- `429`: se agotó temporalmente la cuota; espera y reintenta manualmente.
- Timeout/sin red: la aplicación no reenvía automáticamente la petición para evitar duplicados.

## El overlay no aparece

La protección es fail-closed: si Windows no confirma `WDA_EXCLUDEFROMCAPTURE`, el texto sensible no se muestra. Reintenta con DWM activo y sin modos de compatibilidad. Registra la herramienta de captura y versión usada antes de reportarlo.
