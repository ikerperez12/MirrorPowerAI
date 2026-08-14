# Seguridad

## Controles implementados

- Secretos protegidos con DPAPI `CurrentUser`, nunca en `settings.json`.
- Peticiones HTTPS con un `HttpClient` reutilizado, cancelación y timeout; las redirecciones automáticas están desactivadas antes de añadir la API key de Gemini.
- Sin reintentos automáticos de peticiones POST a Gemini.
- Respuestas externas renderizadas únicamente como texto plano.
- Modelo Whisper descargado con URL fijada, escritura atómica, tamaño y SHA-256 verificados.
- Dependencias NuGet con versiones centrales exactas y lockfiles.
- Restore bloqueado en build/CI y auditoría NuGet para severidad alta/crítica.
- GitHub Actions fijadas a SHA de commit y permisos mínimos (`contents: read`).
- Overlay fail-closed si no puede verificarse la exclusión de captura.

## Modelo de amenazas acotado

La aplicación protege frente a exposición accidental en capturas que respeten DWM y frente a lectura casual de la API key en disco. No pretende resistir a un administrador local, malware en la sesión del usuario, una cámara física, drivers de captura ni herramientas que eludan deliberadamente las APIs públicas de Windows.

## Reporte responsable

Durante esta fase, informa de vulnerabilidades de forma privada al mantenedor del fork. No incluyas API keys, audio real, transcripciones privadas ni volcados completos en issues públicos.
