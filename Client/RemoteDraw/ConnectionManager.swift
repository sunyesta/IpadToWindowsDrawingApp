import CoreGraphics
import Foundation
import Network
import Observation

/// Owns the two TCP connections to the Windows server:
///   video (port 9000): server -> iPad, length-prefixed H.264 messages
///   input (port 9001): iPad -> server, small pen/gesture/key packets
/// Over Wi-Fi the iPad dials the PC. Over USB it's the other way round: the server dials the iPad
/// through Apple's usbmuxd, which arrives here as a connection to a loopback listener on the same port.
/// Both use TCP_NODELAY. Everything runs on the main queue: per-frame work here is tiny,
/// the heavy lifting (decode) happens inside AVSampleBufferVideoRenderer.
@MainActor
@Observable
final class ConnectionManager {
    enum Status: Equatable {
        case disconnected, connecting, connected
        case failed(String)
    }

    enum Transport { case wifi, usb }

    private(set) var status: Status = .disconnected
    private(set) var transport: Transport = .wifi
    private(set) var videoSize: CGSize = .zero

    let decoder = H264Decoder()

    private var video: NWConnection?
    private var input: NWConnection?
    private var usbListeners: [NWListener] = []

    init() {
        decoder.onVideoSize = { [weak self] size in self?.videoSize = size }
    }

    func connect(host: String, videoPort: UInt16 = 9000, inputPort: UInt16 = 9001) {
        disconnect()
        transport = .wifi
        status = .connecting

        let endpointHost = NWEndpoint.Host(host.trimmingCharacters(in: .whitespaces))
        let video = NWConnection(host: endpointHost, port: NWEndpoint.Port(rawValue: videoPort)!, using: Self.tcpParameters())
        let input = NWConnection(host: endpointHost, port: NWEndpoint.Port(rawValue: inputPort)!, using: Self.tcpParameters())
        self.video = video
        self.input = input

        start(video)
        start(input)
        readMessageHeader(from: video)
    }

    /// Waits for the server (started with --usb) to connect through the cable. Listens on loopback
    /// only: usbmuxd tunnels arrive there, and nothing on the Wi-Fi network can reach it.
    func listenForUSB(videoPort: UInt16 = 9000, inputPort: UInt16 = 9001) {
        disconnect()
        transport = .usb
        status = .connecting

        for port in [videoPort, inputPort] {
            let params = Self.tcpParameters()
            params.requiredInterfaceType = .loopback
            params.allowLocalEndpointReuse = true
            let listener: NWListener
            do {
                listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
            } catch {
                disconnect()
                status = .failed("USB: \(error.localizedDescription)")
                return
            }
            let isVideo = port == videoPort
            listener.newConnectionHandler = { [weak self] connection in
                MainActor.assumeIsolated { self?.accept(connection, isVideo: isVideo) }
            }
            listener.stateUpdateHandler = { [weak self, weak listener] state in
                MainActor.assumeIsolated {
                    guard case .failed(let error) = state, let self, let listener,
                          self.usbListeners.contains(where: { $0 === listener }) else { return }
                    self.disconnect()
                    self.status = .failed("USB: \(error.localizedDescription)")
                }
            }
            listener.start(queue: .main)
            usbListeners.append(listener)
        }
    }

    func disconnect() {
        for listener in usbListeners { listener.cancel() }
        usbListeners = []
        video?.cancel()
        input?.cancel()
        video = nil
        input = nil
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

    private func start(_ connection: NWConnection) {
        connection.stateUpdateHandler = { [weak self] state in
            MainActor.assumeIsolated { self?.handle(state, of: connection) }
        }
        connection.start(queue: .main)
    }

    /// A tunnel from the server arrived on one of the USB listeners. A newer one replaces an older
    /// one, e.g. when the server restarted before the old connection noticed.
    private func accept(_ connection: NWConnection, isVideo: Bool) {
        if isVideo {
            if let video { drop(video) }
            video = connection
        } else {
            if let input { drop(input) }
            input = connection
        }
        start(connection)
        if isVideo { readMessageHeader(from: connection) }
    }

    private func handle(_ state: NWConnection.State, of connection: NWConnection) {
        guard connection === video || connection === input else { return }  // stale connection
        switch state {
        case .ready:
            if video?.state == .ready && input?.state == .ready { status = .connected }
        case .waiting(let error), .failed(let error):
            fail("\(error.localizedDescription)", connection: connection)
        default:
            break
        }
    }

    private func fail(_ message: String, connection: NWConnection) {
        if transport == .usb && !usbListeners.isEmpty {
            // Over USB the server redials each channel on its own; keep listening for it.
            drop(connection)
            return
        }
        let wasActive = video != nil
        disconnect()
        if wasActive { status = .failed(message) }
    }

    private func drop(_ connection: NWConnection) {
        connection.cancel()
        if connection === video {
            video = nil
            decoder.reset()
        }
        if connection === input { input = nil }
        status = .connecting
    }

    private func readMessageHeader(from connection: NWConnection) {
        connection.receive(minimumIncompleteLength: 4, maximumLength: 4) { [weak self] data, _, _, error in
            MainActor.assumeIsolated {
                guard let self, connection === self.video else { return }
                guard error == nil, let data, data.count == 4 else {
                    self.fail(error?.localizedDescription ?? "Server closed the connection", connection: connection)
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
                    self.fail(error?.localizedDescription ?? "Server closed the connection", connection: connection)
                    return
                }
                self.decoder.handle(message: [UInt8](data))
                self.readMessageHeader(from: connection)
            }
        }
    }
}
