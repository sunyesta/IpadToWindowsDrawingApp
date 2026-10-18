# iPad → Windows Drawing Tablet

Streams the Windows primary display to an iPad and sends the Apple Pencil back as a real
Windows Ink pen (pressure + tilt), with gestures for pan and zoom.

```
Server/   C# (.NET 10) Windows app: Desktop Duplication capture, ffmpeg H.264 encode, input injection
Client/   SwiftUI iPad app source files: hardware H.264 decode, Pencil + gestures
```

---

## 1. About "connect over Bluetooth"

Bluetooth can't carry this, so the design uses Wi-Fi (or USB later):

| | Bluetooth LE (the only Bluetooth an iPad app can use) | 5 GHz Wi-Fi |
|---|---|---|
| Real throughput on iPad | ~0.1–1.5 Mbit/s | 100–600+ Mbit/s |
| Needed for 1080p60 screen video | ~10–20 Mbit/s | ✓ |
| Latency per packet | 7.5–30 ms connection intervals | ~1–3 ms |

iPadOS doesn't let apps use Bluetooth Classic serial or Bluetooth networking (PAN) without
Apple's MFi hardware program. Even the input channel alone would add 15–30 ms of jitter over BLE.

**What to use instead:**
1. **Same 5 GHz / 6 GHz Wi-Fi network** (simplest). Ideally plug the PC into the router with Ethernet.
2. **Direct link, no router:** Windows Settings → Network & internet → **Mobile hotspot** (needs a
   Wi-Fi adapter in the PC; pick the 5 GHz band). Join it from the iPad; the PC is usually `192.168.137.1`.
3. **USB-C cable (later upgrade):** tunnel TCP over USB through Apple's `usbmuxd` (the method used by
   Duet/Astropad). This gives the lowest latency. Only the transport changes; the protocol stays the same.

## 2. Recommended protocol

Two TCP connections, both with `TCP_NODELAY` (Nagle off):

```
            ┌────────── video  TCP :9000 ──────────┐
 Windows    │  [u32 len][type][H.264 frame]  ───▶  │   iPad
 server     │                                      │   client
            │  ◀───  [type][len][pen/key/gesture]  │
            └────────── input  TCP :9001 ──────────┘
```

**Why this design:**
- **H.264 with hardware encode (NVENC/QSV/AMF) and decode (VideoToolbox).** No B-frames,
  zero-latency tuning, a key frame every 2 s. Each frame goes out as soon as it is encoded.
- **Separate input socket.** A 5-byte pen packet never waits behind a 200 KB key frame.
- **TCP, not UDP, for now.** On a LAN, TCP with Nagle off adds almost nothing. A lost UDP packet,
  by contrast, needs forward error correction, key-frame requests and reordering code, and a lost
  "pen up" packet leaves a stroke stuck down. UDP + FEC is a later optimization if Wi-Fi
  retransmits cause visible hitches.
- **No queues.** If the network slows down, back-pressure flows back to the capture loop, which
  then sends the *newest* screen image. You get a lower frame rate, not growing lag.
- **Framing is explicit.** Every frame carries its own length, so the iPad decodes it the moment
  it arrives. A raw H.264 byte stream only marks where a frame *starts*, so the iPad would have
  to wait for the next frame, or worse, wait forever on a still screen.
- **Normalized coordinates (0–1).** The iPad doesn't need to know the PC resolution or DPI.

**Latency budget (typical):** capture ~1–5 ms · encode 2–8 ms · Wi-Fi 2–5 ms · decode/display
8–16 ms. That is **~20–35 ms glass-to-glass**, similar to commercial apps (Astropad, Duet).

**How the Pencil gets in:** the server uses `InjectSyntheticPointerInput` with a synthetic **pen**
device, not `SendInput` mouse events. Krita, Photoshop (Windows Ink), Clip Studio, Rebelle and
others see real pressure and tilt. `--mouse` falls back to plain mouse events.

## 3. Tools to download

### On Windows (server)
| Tool | Get it |
|---|---|
| **.NET 10 SDK** | `winget install Microsoft.DotNet.SDK.10` or <https://dotnet.microsoft.com/download> |
| **ffmpeg** (the encoder) | `winget install Gyan.FFmpeg` (then open a new terminal so `ffmpeg` is on PATH) |
| An editor | **Visual Studio 2026 Community** (".NET desktop development" workload) *or* **VS Code** + the **C# Dev Kit** extension |
| Git (optional) | `winget install Git.Git` |

### For the iPad (client)
Apple only supports building iPad apps with Apple tools:

| Option | What you need |
|---|---|
| **A Mac + Xcode** (recommended) | Any Apple Silicon Mac (a used Mac mini works). Install **Xcode** from the Mac App Store. |
| **Swift Playgrounds on the iPad** (free, no Mac) | Install *Swift Playgrounds* from the App Store, create an **App** project, paste in the files. It can build and run SwiftUI apps directly on the iPad. Missing or debug features vary by version, so if something doesn't build, use Xcode. |
| A rented cloud Mac | MacinCloud and similar services. Workable, but the iPad has to be plugged into a real Mac to install the app, so this is awkward. |

You also need:
- A **free Apple ID** to sign the app for your own iPad. Free signing expires every 7 days and you
  re-run from Xcode. The $99/year developer program removes that limit.
- **iPadOS 17 or newer.** Turn on Settings → Privacy & Security → **Developer Mode** when Xcode asks.
- **Apple Pencil 2 or Apple Pencil Pro** for double-tap. Hover needs an M2 or newer iPad Pro, or a
  Pencil Pro on a supported iPad.

## 4. Run the server

```powershell
cd Server
dotnet run -c Release                          # CPU encoder (libx264), works everywhere
dotnet run -c Release -- --encoder h264_nvenc  # NVIDIA GPU (lowest latency)
dotnet run -c Release -- --encoder h264_qsv    # Intel iGPU
dotnet run -c Release -- --encoder h264_amf    # AMD GPU
dotnet run -c Release -- --ffmpeg-capture --encoder h264_nvenc   # ffmpeg captures on the GPU and draws the cursor
```

Other options: `--bitrate 15` (Mbit/s), `--fps 60`, `--mouse`, `--video-port`, `--input-port`.

**On this PC (RTX 3060 Ti) use `--encoder h264_nvenc`.** Measured at 2560×1600 when this was set up:
the first image reaches a client ~0.5 s after connecting (~0.23 s on reconnect), and each frame
takes ~15 ms from screen change to encoded packet. That figure includes copying the 16 MB frame
into ffmpeg.

When Windows Firewall asks, allow it on **Private networks**. Your Wi-Fi must be set to the
*Private* profile. The server prints the IP addresses to type into the iPad.

## 5. Build the iPad app (Xcode)

1. Xcode → **File ▸ New ▸ Project ▸ iOS ▸ App**. Name it `RemoteDraw`, Interface **SwiftUI**, Language **Swift**.
2. Delete the generated `ContentView.swift` and `RemoteDrawApp.swift`. Drag every file from
   `Client/RemoteDraw/` into the project ("Copy items if needed").
3. Target ▸ **General**: Supported Destinations **iPad**, Minimum Deployments **iOS 17.0**,
   orientation **Landscape Left/Right**.
4. Target ▸ **Info**: add **Privacy - Local Network Usage Description** =
   "Connects to your Windows PC to show its screen." Without it, iPadOS blocks the connection.
5. Target ▸ **Signing & Capabilities**: Team = your Apple ID (Personal Team).
6. Plug in the iPad, select it as the run destination, press ▶.
7. In the app, enter the PC's IP and tap **Connect**. Allow Local Network access when asked.

## 6. Controls

| iPad | Windows |
|---|---|
| Apple Pencil | Windows Ink pen with pressure and tilt (or left mouse with `--mouse`) |
| Pencil hover | Moves the cursor (brush outline preview) |
| Pencil double-tap | Pen mode → sends **E** (eraser), enters Eraser mode |
| In Eraser mode: orange **"Tap for Pen"** button or double-tap again | Sends **B** (brush), back to Pen mode |
| One-finger drag | Middle-mouse drag (pan) |
| Two-finger pinch | Ctrl + mouse wheel at the pinch center (zoom) |

You can change the hotkeys in the top bar. Change the zoom modifier, pinch sensitivity and
pressure curve in `GestureSettings` (`RemoteScreenView.swift`). Photoshop, for example, zooms
with Alt+wheel.

## 7. Known limitations / next steps

- **Cursor:** in C#-capture mode, Desktop Duplication delivers the desktop *without* the mouse
  cursor. Apps that draw their brush outline as a system cursor (Photoshop) won't show it. Use
  `--ffmpeg-capture`, or draw the cursor yourself with `GetFramePointerShape`.
- **Admin windows:** Windows blocks injecting input into elevated apps (Task Manager, installers)
  from a normal process. Run the server as Administrator if you need that. The UAC prompt and
  lock screen can't be captured at all.
- **Zero-copy:** the C# path copies each frame to the CPU and pipes it to ffmpeg. That's fine at
  1080p–1440p. For 4K, use `--ffmpeg-capture` with a GPU encoder (it stays on the GPU end to
  end), or move to an in-process Media Foundation encoder.
- **Discovery:** add Bonjour (`NWBrowser` on iPad, mDNS on Windows) so you don't type IPs.
- **USB:** replace the TCP endpoint with a usbmuxd tunnel for wired, lowest-latency use.
- **Resolution change on Windows** ends the session; just tap Connect again.
