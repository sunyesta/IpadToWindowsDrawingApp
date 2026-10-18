import AVFoundation
import CoreMedia

/// Turns video messages from the server into CMSampleBuffers and hands them to an
/// AVSampleBufferVideoRenderer, which decodes them in hardware (VideoToolbox) and shows each
/// frame the moment it is decoded (no playback clock, no buffering).
///
/// Message payloads (after the type byte):
///   0 = AVCDecoderConfigurationRecord (SPS/PPS)
///   1 = key frame, 2 = delta frame: AVCC (length-prefixed NAL units)
@MainActor
final class H264Decoder {
    weak var renderer: AVSampleBufferVideoRenderer?
    var onVideoSize: ((CGSize) -> Void)?

    private var format: CMVideoFormatDescription?
    private var needsKeyFrame = true

    func reset() {
        format = nil
        needsKeyFrame = true
        renderer?.flush()
    }

    func handle(message: [UInt8]) {
        guard let type = message.first else { return }
        let payload = message[1...]
        switch type {
        case 0: configure(avcC: Array(payload))
        case 1, 2: decode(frame: payload, isKeyFrame: type == 1)
        default: break
        }
    }

    // MARK: - Configuration (SPS/PPS)

    private func configure(avcC: [UInt8]) {
        guard avcC.count >= 7 else { return }
        let nalLengthSize = Int32(avcC[4] & 0x03) + 1
        var parameterSets: [[UInt8]] = []
        var i = 5

        func readSets(count: Int) -> Bool {
            for _ in 0..<count {
                guard i + 2 <= avcC.count else { return false }
                let length = Int(avcC[i]) << 8 | Int(avcC[i + 1])
                i += 2
                guard i + length <= avcC.count else { return false }
                parameterSets.append(Array(avcC[i..<i + length]))
                i += length
            }
            return true
        }

        let spsCount = Int(avcC[i] & 0x1F); i += 1
        guard readSets(count: spsCount), i < avcC.count else { return }
        let ppsCount = Int(avcC[i]); i += 1
        guard readSets(count: ppsCount), parameterSets.count >= 2 else { return }

        // CoreMedia wants an array of pointers; lay the sets out in one buffer to keep them alive.
        let joined = parameterSets.flatMap { $0 }
        let sizes = parameterSets.map(\.count)
        var newFormat: CMFormatDescription?
        let status = joined.withUnsafeBufferPointer { buffer -> OSStatus in
            var pointers: [UnsafePointer<UInt8>] = []
            var offset = 0
            for size in sizes {
                pointers.append(buffer.baseAddress! + offset)
                offset += size
            }
            return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                allocator: kCFAllocatorDefault,
                parameterSetCount: parameterSets.count,
                parameterSetPointers: pointers,
                parameterSetSizes: sizes,
                nalUnitHeaderLength: nalLengthSize,
                formatDescriptionOut: &newFormat)
        }
        guard status == noErr, let newFormat else {
            print("H264Decoder: bad SPS/PPS (\(status))")
            return
        }

        format = newFormat
        needsKeyFrame = true
        renderer?.flush()
        let dims = CMVideoFormatDescriptionGetDimensions(newFormat)
        onVideoSize?(CGSize(width: Int(dims.width), height: Int(dims.height)))
    }

    // MARK: - Frames

    private func decode(frame: ArraySlice<UInt8>, isKeyFrame: Bool) {
        guard let format, let renderer else { return }

        // After a decode error (e.g. the app was backgrounded) the renderer must be flushed
        // and can only resume from a key frame. The server sends one every 2 seconds.
        if renderer.status == .failed || renderer.requiresFlushToResumeDecoding {
            renderer.flush()
            needsKeyFrame = true
        }
        if needsKeyFrame {
            guard isKeyFrame else { return }
            needsKeyFrame = false
        }

        guard let sample = makeSampleBuffer(frame, format: format) else { return }
        renderer.enqueue(sample)
    }

    private func makeSampleBuffer(_ frame: ArraySlice<UInt8>, format: CMVideoFormatDescription) -> CMSampleBuffer? {
        let length = frame.count
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: length,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
            dataLength: length, flags: kCMBlockBufferAssureMemoryNowFlag, blockBufferOut: &block) == noErr,
              let block else { return nil }

        let copied = frame.withUnsafeBytes {
            CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block,
                                          offsetIntoDestination: 0, dataLength: length)
        }
        guard copied == noErr else { return nil }

        var sample: CMSampleBuffer?
        var sampleSize = length
        guard CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format,
            sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
            sampleSizeEntryCount: 1, sampleSizeArray: &sampleSize, sampleBufferOut: &sample) == noErr,
              let sample else { return nil }

        // No timestamps: show every frame as soon as it is decoded.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true) as? [NSMutableDictionary],
           let first = attachments.first {
            first[kCMSampleAttachmentKey_DisplayImmediately] = true
        }
        return sample
    }
}
