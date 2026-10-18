import AVFoundation
import SwiftUI
import UIKit

struct GestureSettings {
    /// Modifiers held while the pinch sends wheel notches. Ctrl+wheel zooms in Krita, Clip Studio
    /// and most Windows apps; Photoshop uses Alt+wheel.
    var zoomModifiers: KeyModifiers = .ctrl
    /// How much finger spread (as a scale factor) equals one wheel notch.
    var zoomStepScale: CGFloat = 1.12
    /// Exponent applied to Pencil pressure (below 1 = lighter touch gives more pressure).
    var pressureCurve: Float = 1.0
}

/// SwiftUI wrapper around the UIKit view that shows the stream and captures input.
struct RemoteScreenView: UIViewRepresentable {
    let connection: ConnectionManager
    var settings = GestureSettings()
    var onPencilDoubleTap: () -> Void

    func makeUIView(context: Context) -> RemoteScreenUIView {
        let view = RemoteScreenUIView()
        connection.decoder.renderer = view.displayLayer.sampleBufferRenderer
        view.onInput = { [connection] events in connection.send(events) }
        return view
    }

    func updateUIView(_ view: RemoteScreenUIView, context: Context) {
        view.videoSize = connection.videoSize
        view.settings = settings
        view.onPencilDoubleTap = onPencilDoubleTap
    }
}

/// Shows the video and turns touches into InputEvents:
///   Apple Pencil      -> pen (pressure, tilt, hover), via touchesBegan/Moved/Ended
///   1-finger drag     -> middle-mouse drag (pan)
///   2-finger pinch    -> Ctrl + wheel (zoom)
///   Pencil double-tap -> onPencilDoubleTap (tool switching lives in ContentView)
final class RemoteScreenUIView: UIView, UIPencilInteractionDelegate, UIGestureRecognizerDelegate {
    override class var layerClass: AnyClass { AVSampleBufferDisplayLayer.self }
    var displayLayer: AVSampleBufferDisplayLayer { layer as! AVSampleBufferDisplayLayer }

    var videoSize: CGSize = .zero
    var settings = GestureSettings()
    var onInput: (([InputEvent]) -> Void)?
    var onPencilDoubleTap: (() -> Void)?

    private var penTouch: UITouch?
    private var panRecognizer: UIPanGestureRecognizer!
    private var lastPinchScale: CGFloat = 1
    private var pinchAccumulator: CGFloat = 0

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = .black
        isMultipleTouchEnabled = true
        displayLayer.videoGravity = .resizeAspect

        let pencil = UIPencilInteraction()
        pencil.delegate = self
        addInteraction(pencil)

        // Pencil hover (iPad Pro M2 and later) moves the Windows cursor, so brush outlines show.
        addGestureRecognizer(UIHoverGestureRecognizer(target: self, action: #selector(handleHover(_:))))

        let fingersOnly = [NSNumber(value: UITouch.TouchType.direct.rawValue)]

        panRecognizer = UIPanGestureRecognizer(target: self, action: #selector(handlePan(_:)))
        panRecognizer.maximumNumberOfTouches = 1
        panRecognizer.allowedTouchTypes = fingersOnly
        panRecognizer.delegate = self
        addGestureRecognizer(panRecognizer)

        let pinch = UIPinchGestureRecognizer(target: self, action: #selector(handlePinch(_:)))
        pinch.allowedTouchTypes = fingersOnly
        pinch.delegate = self
        addGestureRecognizer(pinch)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    // MARK: - Coordinates

    /// The part of the view the video actually occupies (letterboxed, aspect fit).
    private var videoRect: CGRect {
        guard videoSize.width > 0, videoSize.height > 0 else { return bounds }
        return AVMakeRect(aspectRatio: videoSize, insideRect: bounds)
    }

    /// View point -> 0...1 across the Windows screen.
    private func normalized(_ p: CGPoint) -> CGPoint {
        let r = videoRect
        return CGPoint(x: min(max((p.x - r.minX) / r.width, 0), 1),
                       y: min(max((p.y - r.minY) / r.height, 0), 1))
    }

    // MARK: - Apple Pencil

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard penTouch == nil, let touch = touches.first(where: { $0.type == .pencil }) else { return }
        penTouch = touch
        cancelPan()  // a palm or finger started a pan just before the pencil landed
        onInput?([penEvent(touch, phase: .down)])
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let touch = penTouch, touches.contains(touch) else { return }
        // Coalesced touches carry every 240 Hz Pencil sample since the last display frame.
        let samples = event?.coalescedTouches(for: touch) ?? [touch]
        onInput?(samples.map { penEvent($0, phase: .move) })
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let touch = penTouch, touches.contains(touch) else { return }
        penTouch = nil
        onInput?([penEvent(touch, phase: .up)])
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        touchesEnded(touches, with: event)
    }

    private func penEvent(_ touch: UITouch, phase: PenPhase) -> InputEvent {
        let point = normalized(touch.preciseLocation(in: self))
        var pressure: Float = 0
        if phase != .up {
            pressure = touch.maximumPossibleForce > 0 ? Float(touch.force / touch.maximumPossibleForce) : 1
            pressure = pow(min(max(pressure, 0), 1), settings.pressureCurve)
        }
        let (tiltX, tiltY) = Self.windowsTilt(altitude: touch.altitudeAngle, azimuth: touch.azimuthAngle(in: self))
        return .pen(phase, point, pressure: pressure, tiltX: tiltX, tiltY: tiltY)
    }

    /// UIKit gives altitude (0 = flat, pi/2 = upright) and azimuth (direction the barrel points).
    /// Windows wants tilt from vertical along X (+ = right) and Y (+ = toward the user), in degrees.
    private static func windowsTilt(altitude: CGFloat, azimuth: CGFloat) -> (Float, Float) {
        let fromVertical = min(CGFloat.pi / 2 - altitude, 1.55)
        let t = tan(fromVertical)
        let toDegrees = 180 / CGFloat.pi
        return (Float(atan(t * cos(azimuth)) * toDegrees), Float(atan(t * sin(azimuth)) * toDegrees))
    }

    @objc private func handleHover(_ g: UIHoverGestureRecognizer) {
        guard penTouch == nil else { return }
        let point = normalized(g.location(in: self))
        switch g.state {
        case .began, .changed:
            onInput?([.pen(.hover, point, pressure: 0, tiltX: 0, tiltY: 0)])
        case .ended, .cancelled:
            onInput?([.pen(.leave, point, pressure: 0, tiltX: 0, tiltY: 0)])
        default:
            break
        }
    }

    // Pencil double-tap. iPadOS 17.5 replaced pencilInteractionDidTap with didReceiveTap.
    @available(iOS 17.5, *)
    func pencilInteraction(_ interaction: UIPencilInteraction, didReceiveTap tap: UIPencilInteraction.Tap) {
        onPencilDoubleTap?()
    }

    func pencilInteractionDidTap(_ interaction: UIPencilInteraction) {
        if #available(iOS 17.5, *) { return }  // handled above
        onPencilDoubleTap?()
    }

    // MARK: - Finger gestures

    func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        penTouch == nil  // no panning/zooming mid-stroke (palm rejection)
    }

    func gestureRecognizer(_ g: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        true  // lets a pinch take over from a pan that already started
    }

    private func cancelPan() {
        guard panRecognizer.state == .began || panRecognizer.state == .changed else { return }
        panRecognizer.isEnabled = false  // toggling cancels it; handlePan sends the middle-button release
        panRecognizer.isEnabled = true
    }

    @objc private func handlePan(_ g: UIPanGestureRecognizer) {
        let location = g.location(in: self)
        switch g.state {
        case .began:
            // Press where the finger first touched, not where the pan was recognized.
            let t = g.translation(in: self)
            let start = CGPoint(x: location.x - t.x, y: location.y - t.y)
            onInput?([.middleDrag(.begin, normalized(start)), .middleDrag(.move, normalized(location))])
        case .changed:
            onInput?([.middleDrag(.move, normalized(location))])
        case .ended, .cancelled, .failed:
            onInput?([.middleDrag(.end, normalized(location))])
        default:
            break
        }
    }

    @objc private func handlePinch(_ g: UIPinchGestureRecognizer) {
        switch g.state {
        case .began:
            cancelPan()
            lastPinchScale = g.scale
            pinchAccumulator = 0
        case .changed:
            // Work in log space so spreading and pinching by the same ratio are symmetric.
            pinchAccumulator += log(g.scale / lastPinchScale)
            lastPinchScale = g.scale
            let notch = log(settings.zoomStepScale)
            let center = normalized(g.location(in: self))
            var events: [InputEvent] = []
            while pinchAccumulator >= notch {
                events.append(.scroll(center, delta: 120, modifiers: settings.zoomModifiers))
                pinchAccumulator -= notch
            }
            while pinchAccumulator <= -notch {
                events.append(.scroll(center, delta: -120, modifiers: settings.zoomModifiers))
                pinchAccumulator += notch
            }
            onInput?(events)
        default:
            break
        }
    }
}
