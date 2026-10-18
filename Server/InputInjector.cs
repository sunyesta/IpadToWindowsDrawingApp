using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DrawingServer;

/// <summary>
/// Turns iPad events into Windows input.
///
/// The Pencil is injected as a real Windows Ink pen (InjectSyntheticPointerInput, Windows 10 1809+),
/// so drawing apps receive pressure and tilt exactly as from a Wacom/Surface pen.
/// With --mouse it falls back to SendInput mouse events (position + click only).
///
/// Coordinates arrive normalized (0..1) and are mapped onto the primary monitor in physical
/// pixels, which is why Program.cs makes the process per-monitor DPI aware.
/// </summary>
public sealed class InputInjector
{
    private readonly bool _mouseMode;
    private readonly IntPtr _penDevice;
    private uint _frameId;
    private bool _penInContact;
    private bool _middleDown;
    private PenPhase _lastPenPhase = PenPhase.Leave;

    public InputInjector(bool mouseMode)
    {
        _mouseMode = mouseMode;
        if (!mouseMode)
        {
            _penDevice = Native.CreateSyntheticPointerDevice(Native.PT_PEN, 1, Native.POINTER_FEEDBACK_DEFAULT);
            if (_penDevice == IntPtr.Zero)
            {
                Console.WriteLine($"Input: could not create a synthetic pen ({new Win32Exception().Message}); using mouse mode.");
                _mouseMode = true;
            }
        }
    }

    private static (int X, int Y) ToScreen(float nx, float ny)
    {
        int w = Native.GetSystemMetrics(Native.SM_CXSCREEN), h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        return ((int)Math.Round(Math.Clamp(nx, 0f, 1f) * (w - 1)), (int)Math.Round(Math.Clamp(ny, 0f, 1f) * (h - 1)));
    }

    // ---------------------------------------------------------------- Pen

    /// <param name="pressure">0..1</param>
    /// <param name="tiltX">degrees, -90..90</param>
    /// <param name="tiltY">degrees, -90..90</param>
    public void Pen(PenPhase phase, float nx, float ny, float pressure, float tiltX, float tiltY)
    {
        var (x, y) = ToScreen(nx, ny);
        if (_mouseMode) { PenAsMouse(phase, x, y); return; }

        uint flags = Native.POINTER_FLAG_PRIMARY;
        int buttonChange = 0;
        switch (phase)
        {
            case PenPhase.Down:
                // Windows rejects a pen that touches down without first coming into range. iPads without
                // Pencil hover send Down first, so bring the pen into range here.
                if (_lastPenPhase == PenPhase.Leave) Pen(PenPhase.Hover, nx, ny, 0, tiltX, tiltY);
                flags |=Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT | Native.POINTER_FLAG_FIRSTBUTTON | Native.POINTER_FLAG_DOWN;
                buttonChange = Native.POINTER_CHANGE_FIRSTBUTTON_DOWN;
                _penInContact = true;
                break;
            case PenPhase.Move:
                if (!_penInContact) { Pen(PenPhase.Down, nx, ny, pressure, tiltX, tiltY); return; }
                flags |= Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_INCONTACT | Native.POINTER_FLAG_FIRSTBUTTON | Native.POINTER_FLAG_UPDATE;
                break;
            case PenPhase.Up:
                if (!_penInContact) return;
                flags |= Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_UP;
                buttonChange = Native.POINTER_CHANGE_FIRSTBUTTON_UP;
                pressure = 0;
                _penInContact = false;
                break;
            case PenPhase.Hover:
                if (_penInContact) return;            // stray hover while drawing
                flags |= Native.POINTER_FLAG_INRANGE | Native.POINTER_FLAG_UPDATE;
                pressure = 0;
                break;
            case PenPhase.Leave:
                if (_penInContact || _lastPenPhase == PenPhase.Leave) return;
                flags |= Native.POINTER_FLAG_UPDATE;  // UPDATE without INRANGE = pen left hover range
                pressure = 0;
                break;
        }
        _lastPenPhase = phase;

        var info = new Native.POINTER_TYPE_INFO
        {
            type = Native.PT_PEN,
            penInfo = new Native.POINTER_PEN_INFO
            {
                pointerInfo = new Native.POINTER_INFO
                {
                    pointerType = Native.PT_PEN,
                    pointerId = 0,
                    frameId = ++_frameId,
                    pointerFlags = flags,
                    ptPixelLocation = new Native.POINT { X = x, Y = y },
                    ButtonChangeType = buttonChange,
                },
                penMask = Native.PEN_MASK_PRESSURE | Native.PEN_MASK_TILT_X | Native.PEN_MASK_TILT_Y,
                pressure = (uint)Math.Round(Math.Clamp(pressure, 0f, 1f) * 1024),
                tiltX = (int)Math.Round(Math.Clamp(tiltX, -90f, 90f)),
                tiltY = (int)Math.Round(Math.Clamp(tiltY, -90f, 90f)),
            },
        };

        if (!Native.InjectSyntheticPointerInput(_penDevice, ref info, 1))
            Console.WriteLine($"Input: pen injection failed ({new Win32Exception().Message})");
    }

    private void PenAsMouse(PenPhase phase, int x, int y)
    {
        switch (phase)
        {
            case PenPhase.Down when !_penInContact:
                _penInContact = true;
                SendMouse(x, y, Native.MOUSEEVENTF_LEFTDOWN);
                break;
            case PenPhase.Up when _penInContact:
                _penInContact = false;
                SendMouse(x, y, Native.MOUSEEVENTF_LEFTUP);
                break;
            case PenPhase.Leave:
                break;
            default:
                SendMouse(x, y, 0);
                break;
        }
    }

    // ---------------------------------------------------------------- Gestures

    public void MiddleDrag(DragPhase phase, float nx, float ny)
    {
        var (x, y) = ToScreen(nx, ny);
        switch (phase)
        {
            case DragPhase.Begin:
                SendMouse(x, y, 0);
                SendMouse(x, y, Native.MOUSEEVENTF_MIDDLEDOWN);
                _middleDown = true;
                break;
            case DragPhase.Move when _middleDown:
                SendMouse(x, y, 0);
                break;
            case DragPhase.End when _middleDown:
                SendMouse(x, y, Native.MOUSEEVENTF_MIDDLEUP);
                _middleDown = false;
                break;
        }
    }

    /// <param name="delta">Wheel delta; 120 = one notch. Positive = away from user (zoom in in most apps).</param>
    public void Scroll(float nx, float ny, short delta, KeyModifiers modifiers)
    {
        var (x, y) = ToScreen(nx, ny);
        SendMouse(x, y, 0);  // most apps zoom around the cursor
        PressModifiers(modifiers, down: true);
        Native.SendInputs(Native.MouseInput(0, 0, Native.MOUSEEVENTF_WHEEL, unchecked((uint)delta)));
        PressModifiers(modifiers, down: false);
    }

    // ---------------------------------------------------------------- Keyboard

    public void Key(ushort vk, KeyModifiers modifiers, KeyAction action)
    {
        if (action != KeyAction.Up) PressModifiers(modifiers, down: true);
        if (action != KeyAction.Up) Native.SendInputs(Native.KeyInput(vk, down: true));
        if (action != KeyAction.Down) Native.SendInputs(Native.KeyInput(vk, down: false));
        if (action != KeyAction.Down) PressModifiers(modifiers, down: false);
    }

    private static void PressModifiers(KeyModifiers m, bool down)
    {
        if (m.HasFlag(KeyModifiers.Ctrl)) Native.SendInputs(Native.KeyInput(Native.VK_CONTROL, down));
        if (m.HasFlag(KeyModifiers.Shift)) Native.SendInputs(Native.KeyInput(Native.VK_SHIFT, down));
        if (m.HasFlag(KeyModifiers.Alt)) Native.SendInputs(Native.KeyInput(Native.VK_MENU, down));
    }

    /// <summary>Called when the iPad disconnects.</summary>
    public void ReleaseAll()
    {
        if (_penInContact) Pen(PenPhase.Up, 0.5f, 0.5f, 0, 0, 0);
        if (!_mouseMode) Pen(PenPhase.Leave, 0.5f, 0.5f, 0, 0, 0);
        if (_middleDown) MiddleDrag(DragPhase.End, 0.5f, 0.5f);
    }

    private static void SendMouse(int x, int y, uint buttonFlags)
    {
        // SendInput absolute coordinates are 0..65535 across the primary monitor.
        int w = Native.GetSystemMetrics(Native.SM_CXSCREEN), h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        int ax = (int)((x * 65535L + w / 2) / Math.Max(1, w - 1));
        int ay = (int)((y * 65535L + h / 2) / Math.Max(1, h - 1));
        Native.SendInputs(Native.MouseInput(ax, ay, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | buttonFlags, 0));
    }
}
