# Integración upstream futura

## Alcance del cambio

- La implementación Windows se concentra en `Windows/`.
- `global.json`, `.github/workflows/ci.yml`, `.github/dependabot.yml` y las reglas `.gitignore` son los únicos cambios compartidos de infraestructura.
- La implementación Swift/macOS no se modifica.

## Estrategia de adopción

1. Sincronizar `main` del fork por fast-forward con `RafaTatay/main`.
2. Rebasear `feat/windows-native` sobre ese punto y resolver únicamente infraestructura compartida.
3. Ejecutar CI macOS y Windows desde un checkout limpio.
4. Completar la matriz manual y adjuntar resultados, limitaciones y capturas sin datos sensibles.
5. Obtener del autor una decisión explícita sobre licencia y distribución.
6. Sólo entonces proponer una PR o una serie de commits cherry-pickables.

No se debe fusionar esta rama en `main` del fork mientras `main` se use como espejo exacto del upstream.
