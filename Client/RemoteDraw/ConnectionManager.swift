import Foundation
import Network
import Observation

/// Owns the two TCP connections to the Windows server:
///   video (port 9000): server -> iPad, length-prefixed H.264 messages
///   input (port 9001): iPad -> server, small pen/gesture/key packets
/// Both use TCP_NODELAY. Everything runs on the main queue: per-frame work here is tiny,
/// the heavy lifting (decode) happens inside AVSampleBufferVideoRenderer.
@MainActor
@Observable
final class ConnectionManager {
    enum Status: Equatable {
        case disconnected, connecting, connected
        case failed(String)
    }

    private(set) var status: Status = .disconnected
    private(set) var videoSize: CGSize = .zero

    let decoder = H264Decoder()

    private var video: NWConnection?
    private var input: NWConnection?
    private var readyCount = 0

    init() {
        decoder.onVideoSize = { [weak self] size in self?.videoSize = size }
    }

    func connect(host: String, videoPort: UInt16 = 9000, inputPort: UInt16 = 9001) {
        disconnect()
        status = .connecting

        let endpointHost = NWEndpoint.Host(host.trimmingCharacters(in: .whitespaces))
        let video = NWConnection(host: endpointHost, port: NWEndpoint.Port(rawValue: videoPort)!, using: Self.tcpParameters())
        let input = NWConnection(host: endpointHost, port: NWEndpoint.Port(rawValue: inputPort)!, using: Self.tcpParameters())
        self.video = video
        self.input = input

        for connection in [video, input] {
            connection.stateUpdateHandler = { [weak self] state in
                MainActor.assumeIsolated { self?.handle(state, of: connection) }
            }
            connection.start(queue: .main)
        }
        readMessageHeader(from: video)
    }

    func disconnect() {
        video?.cancel()
        input?.cancel()
        video = nil
        input = nil
        readyCount = 0
        decoder.reset()
        status = .disconnected
    }

    func send(_ events: [InputEvent]) {
        guard let input, status == .connected, !events.isEmpty else { return }
        var packet = Data()
        for event in events { packet.append(event.encoded()) }
        input.send(content: packet, completion: .idempotent)
    }

    // MARK: - Private

    private static func tcpParameters() -> NWParameters {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.connectionTimeout = 5
        let params = NWParameters(tls: nil, tcp: tcp)
        params.serviceClass = .interactiveVideo
        return params
    }

    private func handle(_ state: NWConnection.State, of connection: NWConnection) {
        guard connection === video || connection === input else { return }  // stale connection
        switch state {
        case .ready:
            readyCount += 1
            if readyCount == 2 { status = .connected }
        case .waiting(let error), .failed(let error):
            fail("\(error.localizedDescription)")
        default:
            break
        }
    }

    private func fail(_ message: String) {
        let wasActive = video != nil
        disconnect()
        if wasActive { status = .failed(message) }
    }

    private func readMessageHeader(from connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 4, maximumLength: 4) { [weak self] data, _, _, error in
            MainActor.assumeIsolated {
                guard let self, connection === self.video else { return }
                guard error == nil, let data, data.count == 4 else {
                    self.fail(error?.localizedDescription ?? "Server closed the connection")
                    return
                }
                let length = data.withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }.bigEndian
                self.readMessageBody(from: connection, length: Int(length))
            }
        }
    }

    private func readMessageBody(from connection: NWConnection, length: Int) {
        connection.receive(minimumIncompleteLength: length, maximumLength: length) { [weak self] data, _, _, error in
            MainActor.assumeIsolated {
                guard let self, connection === self.video else { return }
                guard error == nil, let data, data.count == length else {
                    self.fail(error?.localizedDescription ?? "Server closed the connection")
                    return
                }
                self.decoder.handle(message: [UInt8](data))
                self.readMessageHeader(from: connection)
            }
        }
    }
}
