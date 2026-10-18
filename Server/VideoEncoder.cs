using System.Diagnostics;

namespace DrawingServer;

/// <summary>
/// Runs ffmpeg as a low-latency H.264 encoder.
///   stdin  : raw BGRA frames from <see cref="ScreenCapturer"/> (unless --ffmpeg-capture)
///   stdout : an FLV stream. FLV is used only because it frames every encoded frame with a
///            length header, so we know exactly where each frame ends without waiting for
///            the next one. Its H.264 payload is already in the AVCC layout iOS wants.
/// </summary>
public sealed class VideoEncoder : IDisposable
{
    private readonly Process _process;

    public Stream Input => _process.StandardInput.BaseStream;
    private Stream Output => _process.StandardOutput.BaseStream;

    public VideoEncoder(ServerOptions o, int width, int height)
    {
        var psi = new ProcessStartInfo(o.FfmpegPath, BuildArgs(o, width, height))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"[ffmpeg] {e.Data}"); };
        _process.BeginErrorReadLine();
    }

    private static string BuildArgs(ServerOptions o, int width, int height)
    {
        string rate = $"-b:v {o.BitrateMbps}M -maxrate {o.BitrateMbps}M -bufsize {o.BitrateMbps * 100}k -g {o.Fps * 2} -bf 0";

        // Per-encoder low-latency settings, plus the filter that gets ddagrab's GPU frames into it.
        // Peak-capped VBR, not CBR: GPU encoders pad CBR with filler data, so a still screen
        // would still cost the full bitrate on Wi-Fi.
        (string codec, string ddagrabFilter) = o.Encoder switch
        {
            "h264_nvenc" => ($"-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -delay 0 -rc vbr -cq 23 {rate}", ""),
            "h264_qsv" => ($"-c:v h264_qsv -preset veryfast -async_depth 1 -look_ahead 0 {rate} -pix_fmt nv12",
                           ",hwmap=derive_device=qsv,format=qsv"),
            "h264_amf" => ($"-c:v h264_amf -usage ultralowlatency -quality speed -rc vbr_peak {rate}", ""),
            // zerolatency uses sliced threads, never frame threads: frame threading holds back
            // ~1 frame per thread, which on a still screen means the picture never arrives.
            _ => ($"-c:v libx264 -preset ultrafast -tune zerolatency {rate} -pix_fmt yuv420p",
                  ",hwdownload,format=bgra"),
        };

        string input = o.FfmpegCapture
            ? $"-f lavfi -i ddagrab=output_idx=0:framerate={o.Fps}:draw_mouse=1{ddagrabFilter}"
            : $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {o.Fps} -i pipe:0";

        // Note: no "-fflags nobuffer". It makes ffmpeg discard the frame it read while probing
        // the input, i.e. the first screen image, which on a still desktop is the only one.
        return $"-hide_banner -loglevel warning {input} -an {codec} " +
               "-flush_packets 1 -f flv -flvflags no_duration_filesize pipe:1";
    }

    /// <summary>Reads FLV from ffmpeg and yields (messageType, payload) in our wire format.</summary>
    public async IAsyncEnumerable<(byte Type, byte[] Payload)> ReadMessagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[13];                        // 9-byte FLV header + PreviousTagSize0
        await Output.ReadExactlyAsync(header, ct);
        if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V')
            throw new InvalidDataException("ffmpeg did not produce FLV output.");

        var tagHeader = new byte[11];
        var trailer = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            await Output.ReadExactlyAsync(tagHeader, ct);
            int tagType = tagHeader[0];
            int size = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
            var data = new byte[size];
            await Output.ReadExactlyAsync(data, ct);
            await Output.ReadExactlyAsync(trailer, ct);   // PreviousTagSize

            if (tagType != 9 || size < 5) continue;       // 9 = video; skip script/metadata tags
            if ((data[0] & 0x80) != 0 || (data[0] & 0x0F) != 7)
                throw new InvalidDataException("Expected legacy FLV H.264 (codec id 7).");

            bool keyFrame = (data[0] >> 4) == 1;
            byte avcPacketType = data[1];                 // 0 = config, 1 = NAL units, 2 = end of sequence
            byte[] payload = data[5..];                   // skip frame/codec byte, packet type, 24-bit CTS

            if (avcPacketType == 0) yield return (Protocol.VideoConfig, payload);
            else if (avcPacketType == 1) yield return (keyFrame ? Protocol.VideoKeyFrame : Protocol.VideoDeltaFrame, payload);
        }
    }

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;  // called from two threads on disconnect
        try { if (!_process.HasExited) _process.Kill(); } catch { }
        _process.Dispose();
    }
}
