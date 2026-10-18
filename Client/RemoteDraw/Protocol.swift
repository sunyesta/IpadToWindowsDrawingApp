import CoreGraphics
import Foundation

// Wire format shared with the Windows server (see Server/Protocol.cs).
// Input packets: [u8 type][u8 payloadLength][payload], little-endian, coordinates normalized 0...1.

enum PenPhase: UInt8 { case hover = 0, down, move, up, leave }
enum DragPhase: UInt8 { case begin = 0, move, end }

struct KeyModifiers: OptionSet {
    let rawValue: UInt8
    static let ctrl = KeyModifiers(rawValue: 1)
    static let shift = KeyModifiers(rawValue: 2)
    static let alt = KeyModifiers(rawValue: 4)
}

enum InputEvent {
    case pen(PenPhase, CGPoint, pressure: Float, tiltX: Float, tiltY: Float)
    /// `vk` is a Windows virtual-key code. For A-Z and 0-9 it equals the ASCII code.
    case key(vk: UInt16, modifiers: KeyModifiers = [])
    /// `delta` is a wheel delta: 120 = one notch, positive = zoom in / scroll up.
    case scroll(CGPoint, delta: Int16, modifiers: KeyModifiers)
    case middleDrag(DragPhase, CGPoint)

    func encoded() -> Data {
        var w = PacketWriter()
        switch self {
        case let .pen(phase, p, pressure, tiltX, tiltY):
            w.u8(phase.rawValue); w.u8(0)
            w.f32(Float(p.x)); w.f32(Float(p.y)); w.f32(pressure); w.f32(tiltX); w.f32(tiltY)
            return w.packet(type: 1)
        case let .key(vk, modifiers):
            w.u16(vk); w.u8(modifiers.rawValue); w.u8(0)  // action 0 = tap (down + up)
            return w.packet(type: 2)
        case let .scroll(p, delta, modifiers):
            w.f32(Float(p.x)); w.f32(Float(p.y)); w.u16(UInt16(bitPattern: delta)); w.u8(modifiers.rawValue)
            return w.packet(type: 3)
        case let .middleDrag(phase, p):
            w.u8(phase.rawValue); w.f32(Float(p.x)); w.f32(Float(p.y))
            return w.packet(type: 4)
        }
    }

    static func virtualKey(for letter: String) -> UInt16 {
        UInt16(letter.uppercased().utf8.first ?? UInt8(ascii: "E"))
    }
}

private struct PacketWriter {
    private var payload = Data()

    mutating func u8(_ v: UInt8) { payload.append(v) }
    mutating func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { payload.append(contentsOf: $0) } }
    mutating func f32(_ v: Float) { withUnsafeBytes(of: v.bitPattern.littleEndian) { payload.append(contentsOf: $0) } }

    func packet(type: UInt8) -> Data {
        var d = Data([type, UInt8(payload.count)])
        d.append(payload)
        return d
    }
}
