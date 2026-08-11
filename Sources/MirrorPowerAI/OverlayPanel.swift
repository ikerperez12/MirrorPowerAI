import AppKit
import SwiftUI

/// A floating panel that is excluded from screen capture / screen sharing.
///
/// `sharingType = .none` tells the window server to omit this window from any
/// screen-capture stream (Zoom/Meet/Teams screen share, QuickTime screen
/// recording, OBS, CGWindowListCreateImage, ScreenCaptureKit, etc). This is a
/// standard, documented AppKit API — the same mechanism apps like password
/// managers use to keep sensitive fields out of screenshots. It does NOT hide
/// the window from someone looking at your physical monitor or filming it.
final class OverlayPanel: NSPanel {
    private let viewModel = OverlayViewModel()

    init() {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 420, height: 260),
            styleMask: [.nonactivatingPanel, .titled, .closable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        titlebarAppearsTransparent = true
        titleVisibility = .hidden
        isMovableByWindowBackground = true
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        sharingType = .none // <- excluded from all screen-capture APIs

        contentView = NSHostingView(rootView: OverlayContentView(viewModel: viewModel))

        if let screen = NSScreen.main {
            let x = screen.visibleFrame.maxX - frame.width - 24
            let y = screen.visibleFrame.maxY - frame.height - 24
            setFrameOrigin(NSPoint(x: x, y: y))
        }
    }

    func showStatus(_ text: String) {
        viewModel.status = text
        viewModel.answer = nil
        present()
    }

    func showQuestion(_ text: String) {
        viewModel.question = text
        present()
    }

    func showAnswer(_ text: String) {
        viewModel.status = nil
        viewModel.answer = text
        present()
    }

    private func present() {
        orderFrontRegardless()
    }
}

private final class OverlayViewModel: ObservableObject {
    @Published var status: String? = nil
    @Published var question: String? = nil
    @Published var answer: String? = nil
}

private struct OverlayContentView: View {
    @ObservedObject var viewModel: OverlayViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: "eye.slash.fill")
                Text("MirrorPowerAI — visible solo para ti")
                    .font(.caption).bold()
            }
            .foregroundStyle(.secondary)

            if let question = viewModel.question {
                Text(question)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
            }

            Divider()

            ScrollView {
                if let answer = viewModel.answer {
                    Text(answer)
                        .font(.body)
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                } else if let status = viewModel.status {
                    HStack {
                        ProgressView().controlSize(.small)
                        Text(status)
                    }
                }
            }
        }
        .padding(16)
        .frame(minWidth: 380, minHeight: 220)
        .background(.ultraThickMaterial)
    }
}
