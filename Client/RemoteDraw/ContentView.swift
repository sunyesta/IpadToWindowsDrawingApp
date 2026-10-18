import SwiftUI

enum ToolMode { case pen, eraser }

struct ContentView: View {
    @State private var connection = ConnectionManager()
    @State private var mode: ToolMode = .pen
    @State private var showControls = true

    @AppStorage("host") private var host = "192.168.1.10"
    @AppStorage("eraserKey") private var eraserKey = "E"
    @AppStorage("penKey") private var penKey = "B"

    var body: some View {
        ZStack {
            RemoteScreenView(connection: connection, onPencilDoubleTap: pencilDoubleTapped)
                .ignoresSafeArea()

            VStack {
                topBar
                Spacer()
                if mode == .eraser { backToPenButton }
            }
            .padding()
        }
        .statusBarHidden()
        .persistentSystemOverlays(.hidden)
        .defersSystemGestures(on: .all)   // keep edge swipes from stealing Pencil strokes
        .onAppear { UIApplication.shared.isIdleTimerDisabled = true }
    }

    // MARK: - Pen / eraser switching

    /// Double-tap: Pen -> Eraser (sends the eraser hotkey); in Eraser mode it goes back to Pen.
    private func pencilDoubleTapped() {
        switch mode {
        case .pen:
            connection.send([.key(vk: InputEvent.virtualKey(for: eraserKey))])
            mode = .eraser
        case .eraser:
            switchToPen()
        }
    }

    private func switchToPen() {
        connection.send([.key(vk: InputEvent.virtualKey(for: penKey))])
        mode = .pen
    }

    private var backToPenButton: some View {
        Button(action: switchToPen) {
            Label("Eraser on. Tap for Pen", systemImage: "pencil.tip")
                .font(.headline)
                .padding(.horizontal, 20)
                .padding(.vertical, 12)
        }
        .buttonStyle(.borderedProminent)
        .tint(.orange)
    }

    // MARK: - Connection bar

    private var topBar: some View {
        HStack(spacing: 12) {
            Button {
                withAnimation { showControls.toggle() }
            } label: {
                Image(systemName: showControls ? "chevron.up.circle.fill" : "chevron.down.circle.fill")
                    .font(.title2)
            }

            Circle().fill(statusColor).frame(width: 10, height: 10)

            if showControls {
                TextField("Windows PC IP", text: $host)
                    .textFieldStyle(.roundedBorder)
                    .keyboardType(.numbersAndPunctuation)
                    .autocorrectionDisabled()
                    .frame(width: 180)

                Button(connection.status == .disconnected || isFailed ? "Connect" : "Disconnect") {
                    if connection.status == .disconnected || isFailed {
                        connection.connect(host: host)
                    } else {
                        connection.disconnect()
                    }
                }
                .buttonStyle(.borderedProminent)

                LabeledContent("Eraser key") { keyField($eraserKey) }
                LabeledContent("Pen key") { keyField($penKey) }

                Text(statusText).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(10)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 12))
        .frame(maxWidth: showControls ? .infinity : 90, alignment: .leading)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func keyField(_ binding: Binding<String>) -> some View {
        TextField("", text: binding)
            .textFieldStyle(.roundedBorder)
            .textInputAutocapitalization(.characters)
            .frame(width: 44)
            .onChange(of: binding.wrappedValue) { _, new in
                if new.count > 1 { binding.wrappedValue = String(new.suffix(1)) }
            }
    }

    private var isFailed: Bool {
        if case .failed = connection.status { return true }
        return false
    }

    private var statusColor: Color {
        switch connection.status {
        case .connected: .green
        case .connecting: .yellow
        case .disconnected: .gray
        case .failed: .red
        }
    }

    private var statusText: String {
        switch connection.status {
        case .connected: "Connected \(Int(connection.videoSize.width))x\(Int(connection.videoSize.height))"
        case .connecting: "Connecting..."
        case .disconnected: "Not connected"
        case .failed(let message): message
        }
    }
}
