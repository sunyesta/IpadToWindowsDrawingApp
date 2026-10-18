using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DrawingServer;

/// <summary>
/// Accepts one iPad at a time on the video port. Each connection gets a fresh encoder,
/// so the stream always starts with SPS/PPS and a key frame of the current screen.
///
/// There are no frame queues anywhere: if Wi-Fi is slow, the socket write blocks, which blocks
/// ffmpeg, which blocks the capture loop, and Desktop Duplication simply hands us the newest
/// image next time. Latency stays bounded instead of piling up.
/// </summary>
public sealed class VideoServer(ServerOptions options)
{
    private ScreenCapturer? _capturer;  // lives across sessions, see ScreenCapturer remarks

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, options.VideoPort);
        listener.Start();
        Console.WriteLine($"Video: listening on TCP {options.VideoPort}");

        while (!ct.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(ct);
            client.NoDelay = true;
            client.SendBufferSize = 256 * 1024;
            Console.WriteLine($"Video: iPad connected from {client.Client.RemoteEndPoint}");
            try
            {
                await StreamToAsync(client.GetStream(), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Network drop, ffmpeg exit, capture failure... log it and wait for the next connection.
                Console.WriteLine($"Video: session ended ({e.Message})");
            }
        }
    }

    private async Task StreamToAsync(NetworkStream network, CancellationToken outerCt)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = sessionCts.Token;

        ScreenCapturer? capturer = null;
        if (!options.FfmpegCapture)
        {
            if (_capturer == null || _capturer.ResolutionChanged)
            {
                _capturer?.Dispose();
                _capturer = new ScreenCapturer();
            }
            capturer = _capturer;
        }
        int width = capturer?.Width ?? 0, height = capturer?.Height ?? 0;
        using var encoder = new VideoEncoder(options, width, height);

        Thread? captureThread = null;
        if (capturer != null)
        {
            Console.WriteLine($"Video: capturing {width}x{height} @ {options.Fps} fps, encoder {options.Encoder}");
            captureThread = new Thread(() => CaptureLoop(capturer, encoder, ct)) { IsBackground = true, Name = "Capture" };
            captureThread.Start();
        }

        // The iPad never sends on this socket, so a completed read means it went away. Without this,
        // a still screen means no writes, so a dead connection would never be noticed and the next
        // connect would wait. Killing ffmpeg ends the read loop below.
        _ = WatchForDisconnectAsync(network, sessionCts, encoder);

        try
        {
            await foreach (var (type, payload) in encoder.ReadMessagesAsync(ct))
            {
                var message = new byte[5 + payload.Length];
                BinaryPrimitives.WriteUInt32BigEndian(message, (uint)(payload.Length + 1));
                message[4] = type;
                payload.CopyTo(message, 5);
                await network.WriteAsync(message, ct);    // one write per frame
            }
        }
        finally
        {
            sessionCts.Cancel();
            encoder.Dispose();                            // unblocks the capture thread's pipe write
            captureThread?.Join();                        // the capturer itself is kept for the next session
        }
    }

    private static async Task WatchForDisconnectAsync(NetworkStream network, CancellationTokenSource session, VideoEncoder encoder)
    {
        try { _ = await network.ReadAsync(new byte[1], session.Token); }  // any result = gone
        catch { }
        if (session.IsCancellationRequested) return;
        session.Cancel();
        encoder.Dispose();
    }

    private void CaptureLoop(ScreenCapturer capturer, VideoEncoder encoder, CancellationToken ct)
    {
        var frame = new byte[capturer.Width * capturer.Height * 4];
        var interval = TimeSpan.FromSeconds(1.0 / options.Fps);
        var clock = Stopwatch.StartNew();
        var lastSent = -interval;
        bool pending = capturer.HasFrame;  // a not-yet-sent image is in the staging texture
                                            // (on reconnect: the last image, sent right away)

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // When a frame is pending, only wait until it is due, so the final image of a
                // stroke is never left unsent just because the screen went still.
                var untilDue = lastSent + interval - clock.Elapsed;
                int timeout = pending ? Math.Max(0, (int)Math.Ceiling(untilDue.TotalMilliseconds)) : 100;

                if (capturer.TryAcquire(timeout)) pending = true;

                if (pending && clock.Elapsed - lastSent >= interval)
                {
                    capturer.CopyTo(frame);
                    encoder.Input.Write(frame);
                    encoder.Input.Flush();
                    lastSent = clock.Elapsed;
                    pending = false;
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"Capture: stopped ({e.Message})");
            encoder.Dispose();  // end the session so the iPad reconnects to a working stream
        }
        catch
        {
            // Session is shutting down; the pipe closing under us is expected.
        }
    }
}
