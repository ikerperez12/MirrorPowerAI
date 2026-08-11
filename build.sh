#!/bin/bash
set -euo pipefail

cd "$(dirname "$0")"

APP_NAME="MirrorPowerAI"
BUILD_DIR=".build/release"
APP_BUNDLE="${APP_NAME}.app"

echo "==> Compilando (swift build -c release)…"
swift build -c release

echo "==> Empaquetando ${APP_BUNDLE}…"
rm -rf "${APP_BUNDLE}"
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

cp "${BUILD_DIR}/${APP_NAME}" "${APP_BUNDLE}/Contents/MacOS/${APP_NAME}"
cp "Resources/Info.plist" "${APP_BUNDLE}/Contents/Info.plist"

echo "==> Firmando (ad-hoc)…"
codesign --force --deep --sign - "${APP_BUNDLE}"

echo "==> Listo: $(pwd)/${APP_BUNDLE}"
echo ""
echo "Primer arranque:"
echo "  1. open '${APP_BUNDLE}'"
echo "  2. macOS pedirá permiso de 'Speech Recognition' -> Permitir."
echo "  3. Al iniciar la primera escucha pedirá 'Solo grabación del audio del sistema'."
echo "     Concédelo en Ajustes del Sistema > Privacidad y Seguridad, y vuelve a abrir la app."
echo "  4. En el icono de la barra de menús: 'Configurar Gemini API Key…'"
