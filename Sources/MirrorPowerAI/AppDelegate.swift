import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var hotKeyManager: HotKeyManager!
    private let audioCapture = AudioCaptureManager()
    private let transcriber = TranscriptionManager()
    private let gemini = GeminiClient()
    private let overlay = OverlayPanel()

    private var isListening = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        setupStatusItem()
        hotKeyManager = HotKeyManager { [weak self] in
            self?.toggleListening()
        }
        transcriber.requestAuthorization { granted in
            if !granted {
                NSLog("MirrorPowerAI: permiso de Reconocimiento de Voz denegado")
            }
        }
    }

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "waveform.circle", accessibilityDescription: "MirrorPowerAI")

        let menu = NSMenu()
        menu.addItem(withTitle: "Iniciar / Detener escucha  (⌥⇧L)", action: #selector(toggleListeningFromMenu), keyEquivalent: "")
        menu.addItem(NSMenuItem.separator())
        menu.addItem(withTitle: "Configurar Gemini API Key…", action: #selector(setAPIKey), keyEquivalent: "")
        menu.addItem(withTitle: "Configurar contexto del sistema…", action: #selector(setContext), keyEquivalent: "")
        menu.addItem(NSMenuItem.separator())
        menu.addItem(withTitle: "Salir", action: #selector(quit), keyEquivalent: "q")
        for item in menu.items { item.target = self }
        statusItem.menu = menu
    }

    @objc private func toggleListeningFromMenu() { toggleListening() }

    @objc private func setAPIKey() {
        let alert = NSAlert()
        alert.messageText = "Gemini API Key"
        alert.informativeText = "Se guarda de forma segura en el Keychain de macOS."
        let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 300, height: 24))
        field.stringValue = KeychainHelper.loadAPIKey() ?? ""
        alert.accessoryView = field
        alert.addButton(withTitle: "Guardar")
        alert.addButton(withTitle: "Cancelar")
        if alert.runModal() == .alertFirstButtonReturn {
            KeychainHelper.saveAPIKey(field.stringValue)
        }
    }

    @objc private func setContext() {
        let alert = NSAlert()
        alert.messageText = "Contexto del sistema"
        alert.informativeText = "Describe brevemente lo que vas a enseñar (arquitectura, features, etc.) para que las respuestas sean más precisas."
        let scrollView = NSTextView.scrollableTextView()
        scrollView.frame = NSRect(x: 0, y: 0, width: 360, height: 160)
        if let textView = scrollView.documentView as? NSTextView {
            textView.string = KeychainHelper.loadContext() ?? ""
            alert.accessoryView = scrollView
            alert.addButton(withTitle: "Guardar")
            alert.addButton(withTitle: "Cancelar")
            if alert.runModal() == .alertFirstButtonReturn {
                KeychainHelper.saveContext(textView.string)
            }
        }
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }

    private func toggleListening() {
        isListening.toggle()
        if isListening {
            startListening()
        } else {
            stopListeningAndAnswer()
        }
    }

    private func startListening() {
        statusItem.button?.image = NSImage(systemSymbolName: "waveform.circle.fill", accessibilityDescription: "Escuchando")
        overlay.showStatus("Escuchando…")
        transcriber.reset()
        audioCapture.start { [weak self] buffer in
            self?.transcriber.append(buffer: buffer)
        }
    }

    private func stopListeningAndAnswer() {
        statusItem.button?.image = NSImage(systemSymbolName: "waveform.circle", accessibilityDescription: "MirrorPowerAI")
        audioCapture.stop()
        overlay.showStatus("Transcribiendo…")
        transcriber.finish { [weak self] transcript in
            guard let self else { return }
            guard let transcript, !transcript.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                self.overlay.showStatus("No se ha captado audio.")
                return
            }
            self.overlay.showQuestion(transcript)
            self.gemini.ask(question: transcript, context: KeychainHelper.loadContext()) { result in
                DispatchQueue.main.async {
                    switch result {
                    case .success(let answer):
                        self.overlay.showAnswer(answer)
                    case .failure(let error):
                        self.overlay.showStatus("Error: \(error.localizedDescription)")
                    }
                }
            }
        }
    }
}
