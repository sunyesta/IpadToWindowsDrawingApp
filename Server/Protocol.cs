namespace DrawingServer;

// Wire format shared with the iPad client (see Client/Protocol.swift).
//
// VIDEO channel (TCP, server -> iPad), one message per encoded unit:
//   [u32 big-endian length N][u8 type][N-1 bytes payload]
//   type 0 = AVCDecoderConfigurationRecord ("avcC": SPS/PPS)
//   type 1 = key frame   (AVCC: length-prefixed NAL units)
//   type 2 = delta frame (AVCC: length-prefixed NAL units)
//
// INPUT channel (TCP, iPad -> server), one message per event:
//   [u8 type][u8 payloadLength][payload], all numbers little-endian.
//   Coordinates are normalized 0..1 across the captured screen.
public static class Protocol
{
    public const byte VideoConfig = 0;
    public const byte VideoKeyFrame = 1;
    public const byte VideoDeltaFrame = 2;

    public const byte InputPen = 1;         // phase u8, flags u8, x f32, y f32, pressure f32, tiltX f32, tiltY f32
    public const byte InputKey = 2;         // vk u16, modifiers u8, action u8
    public const byte InputScroll = 3;      // x f32, y f32, delta i16, modifiers u8
    public const byte InputMiddleDrag = 4;  // phase u8, x f32, y f32
}

public enum PenPhase : byte { Hover = 0, Down = 1, Move = 2, Up = 3, Leave = 4 }
public enum DragPhase : byte { Begin = 0, Move = 1, End = 2 }
public enum KeyAction : byte { Tap = 0, Down = 1, Up = 2 }

[Flags]
public enum KeyModifiers : byte { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }
