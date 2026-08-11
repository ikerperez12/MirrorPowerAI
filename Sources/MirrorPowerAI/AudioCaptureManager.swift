import AudioToolbox
import AVFoundation

/// Captures system audio output (e.g. the other participants' voices coming through
/// Zoom/Meet/Teams) using Core Audio Process Taps (`AudioHardwareCreateProcessTap`,
/// macOS 14.2+). Unlike ScreenCaptureKit, this only requests the dedicated "System
/// Audio Recording Only" permission — no Screen Recording permission needed at all.
///
/// The tap + aggregate device are created once (lazily, on first `start()`) and kept
/// alive for the lifetime of the app. macOS ties the permission grant to that specific
/// tap instance, so recreating it on every listen/stop cycle would re-prompt every
/// time — `start()`/`stop()` only toggle the I/O proc on the already-prepared tap.
final class AudioCaptureManager {
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateDeviceID = AudioObjectID(kAudioObjectUnknown)
    private var format: AVAudioFormat?
    private var deviceProcID: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "com.mirrorpowerai.audiotap", qos: .userInitiated)

    func start(onBuffer: @escaping (AVAudioPCMBuffer) -> Void) {
        do {
            if !tapID.isValid {
                try prepareTap()
            }
            guard let format else { throw AudioTapError("formato de audio no disponible") }

            var procID: AudioDeviceIOProcID?
            var err = AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateDeviceID, queue) { _, inInputData, _, _, _ in
                guard let buffer = AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: inInputData, deallocator: nil) else { return }
                onBuffer(buffer)
            }
            guard err == noErr, let procID else { throw AudioTapError("no se pudo registrar el proceso de E/S (\(err))") }
            deviceProcID = procID

            err = AudioDeviceStart(aggregateDeviceID, procID)
            guard err == noErr else { throw AudioTapError("no se pudo iniciar el dispositivo de audio (\(err))") }
        } catch {
            NSLog("MirrorPowerAI: fallo al iniciar la captura de audio del sistema: \(error.localizedDescription)")
        }
    }

    func stop() {
        guard aggregateDeviceID.isValid, let deviceProcID else { return }
        AudioDeviceStop(aggregateDeviceID, deviceProcID)
        AudioDeviceDestroyIOProcID(aggregateDeviceID, deviceProcID)
        self.deviceProcID = nil
    }

    /// Creates the process tap + aggregate device once. This is what triggers macOS's
    /// "System Audio Recording Only" permission prompt the very first time it runs.
    private func prepareTap() throws {
        let tapDescription = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        tapDescription.uuid = UUID()
        tapDescription.muteBehavior = .unmuted

        var newTapID = AudioObjectID(kAudioObjectUnknown)
        let err1 = AudioHardwareCreateProcessTap(tapDescription, &newTapID)
        guard err1 == noErr else { throw AudioTapError("no se pudo crear el tap de audio (\(err1))") }
        tapID = newTapID

        let outputUID = try AudioObjectID(kAudioObjectSystemObject)
            .readDefaultSystemOutputDevice()
            .readDeviceUID()

        let aggregateDescription: [String: Any] = [
            kAudioAggregateDeviceNameKey: "MirrorPowerAI-Tap",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapUIDKey: tapDescription.uuid.uuidString,
                kAudioSubTapDriftCompensationKey: true
            ]]
        ]

        var newAggregateID = AudioObjectID(kAudioObjectUnknown)
        let err2 = AudioHardwareCreateAggregateDevice(aggregateDescription as CFDictionary, &newAggregateID)
        guard err2 == noErr else { throw AudioTapError("no se pudo crear el dispositivo agregado (\(err2))") }
        aggregateDeviceID = newAggregateID

        var streamDescription = try tapID.readAudioTapStreamBasicDescription()
        guard let format = AVAudioFormat(streamDescription: &streamDescription) else {
            throw AudioTapError("no se pudo crear el formato de audio del tap")
        }
        self.format = format
    }

    private func teardown() {
        if aggregateDeviceID.isValid, let deviceProcID {
            AudioDeviceStop(aggregateDeviceID, deviceProcID)
            AudioDeviceDestroyIOProcID(aggregateDeviceID, deviceProcID)
        }
        deviceProcID = nil

        if aggregateDeviceID.isValid {
            AudioHardwareDestroyAggregateDevice(aggregateDeviceID)
            aggregateDeviceID = AudioObjectID(kAudioObjectUnknown)
        }

        if tapID.isValid {
            AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
        format = nil
    }

    deinit { teardown() }
}

private struct AudioTapError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

private extension AudioObjectID {
    var isValid: Bool { self != AudioObjectID(kAudioObjectUnknown) }

    func readDefaultSystemOutputDevice() throws -> AudioObjectID {
        try read(kAudioHardwarePropertyDefaultSystemOutputDevice, defaultValue: AudioObjectID(kAudioObjectUnknown))
    }

    func readDeviceUID() throws -> String {
        try readString(kAudioDevicePropertyDeviceUID)
    }

    func readAudioTapStreamBasicDescription() throws -> AudioStreamBasicDescription {
        try read(kAudioTapPropertyFormat, defaultValue: AudioStreamBasicDescription())
    }

    func readString(_ selector: AudioObjectPropertySelector) throws -> String {
        try read(selector, defaultValue: "" as CFString) as String
    }

    func read<T>(_ selector: AudioObjectPropertySelector, defaultValue: T) throws -> T {
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var dataSize: UInt32 = 0
        var err = AudioObjectGetPropertyDataSize(self, &address, 0, nil, &dataSize)
        guard err == noErr else { throw AudioTapError("error leyendo tamaño de \(selector): \(err)") }

        var value = defaultValue
        err = withUnsafeMutablePointer(to: &value) { ptr in
            AudioObjectGetPropertyData(self, &address, 0, nil, &dataSize, ptr)
        }
        guard err == noErr else { throw AudioTapError("error leyendo \(selector): \(err)") }
        return value
    }
}
