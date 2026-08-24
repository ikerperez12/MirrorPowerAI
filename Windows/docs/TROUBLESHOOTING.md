# Solución de problemas

## El preflight no encuentra .NET 10.0.400

Ejecuta:

```powershell
.\Windows\install-dotnet.ps1
```

La instalación se realiza en `%USERPROFILE%\.dotnet` y no necesita Developer Mode.

## Falta Visual C++ Redistributable

Whisper local requiere Microsoft Visual C++ Redistributable 2015-2022 x64. Instala el paquete oficial de Microsoft y vuelve a ejecutar `Windows\preflight.ps1`.

## El runtime Whisper CPU x64 no carga

Ejecuta `Windows\verify-whisper-runtime.ps1`. Si falla, confirma que estás usando el portable completo y no sólo el `.exe`, que la carpeta `runtimes\win-x64` conserva sus cuatro DLL y que el Visual C++ Redistributable x64 pasa `Windows\preflight.ps1`. No copies DLL desde otra versión ni desde una arquitectura diferente; vuelve a publicar con `Windows\build.ps1` para restaurar el inventario fijado.

## El hotkey no funciona

Otra aplicación puede haber registrado `Alt+Shift+L`. Cierra la aplicación en conflicto y reinicia MirrorPowerAI. El registro usa `MOD_NOREPEAT` para evitar repeticiones mientras se mantienen las teclas.

## No se captura audio

- Comprueba que existe reproducción audible en el dispositivo de salida seleccionado.
- Evita cambiar o desconectar el dispositivo durante una captura.
- El contenido protegido por DRM puede no estar disponible mediante WASAPI loopback.
- Detén la sesión y selecciona de nuevo el dispositivo en Configuración.

## Whisper no descarga o rechaza el modelo

La aplicación descarta cualquier archivo cuyo tamaño o SHA-256 no coincidan con la versión fijada. Revisa conectividad, espacio libre y software de seguridad; después vuelve a intentarlo. No renombres manualmente una descarga parcial.

## Gemini devuelve 401, 429 o timeout

- `401`: revisa la API key.
- `429`: se agotó temporalmente la cuota; espera y reintenta manualmente.
- Timeout/sin red: la aplicación no reenvía automáticamente la petición para evitar duplicados.

## El overlay no aparece

La protección es fail-closed: si Windows no confirma `WDA_EXCLUDEFROMCAPTURE`, el texto sensible no se muestra. Reintenta con DWM activo y sin modos de compatibilidad. Registra la herramienta de captura y versión usada antes de reportarlo.
