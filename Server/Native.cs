using System.Runtime.InteropServices;

namespace DrawingServer;

/// <summary>Win32 declarations for SendInput and synthetic pointer (pen) injection.</summary>
internal static class Native
{
    // ------------------------------------------------------------ DPI / metrics
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
    public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    // ------------------------------------------------------------ SendInput
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
                      MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
                      MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public static void SendInputs(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    public static INPUT MouseInput(int dx, int dy, uint flags, uint data) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, mouseData = data } } };

    public static INPUT KeyInput(ushort vk, bool down) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP } } };

    // ------------------------------------------------------------ Synthetic pointer (pen)
    public const uint PT_PEN = 3;
    public const int POINTER_FEEDBACK_DEFAULT = 1;

    public const uint POINTER_FLAG_NEW = 0x1, POINTER_FLAG_INRANGE = 0x2, POINTER_FLAG_INCONTACT = 0x4,
                      POINTER_FLAG_FIRSTBUTTON = 0x10, POINTER_FLAG_PRIMARY = 0x2000,
                      POINTER_FLAG_DOWN = 0x10000, POINTER_FLAG_UPDATE = 0x20000, POINTER_FLAG_UP = 0x40000;
    public const int POINTER_CHANGE_FIRSTBUTTON_DOWN = 1, POINTER_CHANGE_FIRSTBUTTON_UP = 2;
    public const uint PEN_MASK_PRESSURE = 0x1, PEN_MASK_ROTATION = 0x2, PEN_MASK_TILT_X = 0x4, PEN_MASK_TILT_Y = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penFlags, penMask, pressure, rotation;
        public int tiltX, tiltY;
    }

    /// <summary>
    /// The native struct is { type; union { POINTER_TOUCH_INFO; POINTER_PEN_INFO; } }.
    /// Touch info is 24 bytes larger than pen info, hence the padding (total 152 bytes on x64).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TYPE_INFO
    {
        public uint type;
        public POINTER_PEN_INFO penInfo;
        private long _pad0, _pad1, _pad2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool InjectSyntheticPointerInput(IntPtr device, ref POINTER_TYPE_INFO pointerInfo, uint count);
}
