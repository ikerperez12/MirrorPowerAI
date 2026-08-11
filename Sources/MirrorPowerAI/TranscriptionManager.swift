import Speech
import AVFoundation

/// Streams captured audio buffers into Apple's on-device/server speech recognizer
/// and produces a final transcript when the listening segment ends.
final class TranscriptionManager {
    private let recognizer = SFSpeechRecognizer(locale: Locale(identifier: "es-ES"))
    private var request: SFSpeechAudioBufferRecognitionRequest?
    private var task: SFSpeechRecognitionTask?
    private var latestTranscript: String = ""

    func requestAuthorization(completion: @escaping (Bool) -> Void) {
        SFSpeechRecognizer.requestAuthorization { status in
            DispatchQueue.main.async {
                completion(status == .authorized)
            }
        }
    }

    func reset() {
        latestTranscript = ""
        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = true
        request.requiresOnDeviceRecognition = false
        self.request = request

        task = recognizer?.recognitionTask(with: request) { [weak self] result, error in
            guard let self else { return }
            if let result {
                self.latestTranscript = result.bestTranscription.formattedString
            }
            if error != nil {
                self.task = nil
            }
        }
    }

    func append(buffer: AVAudioPCMBuffer) {
        request?.append(buffer)
    }

    func finish(completion: @escaping (String?) -> Void) {
        request?.endAudio()
        // Give the recognizer a brief moment to flush the final partial result.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
            completion(self?.latestTranscript)
            self?.request = nil
            self?.task = nil
        }
    }
}
