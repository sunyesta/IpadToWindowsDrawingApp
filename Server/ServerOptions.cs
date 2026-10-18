namespace DrawingServer;

public sealed class ServerOptions
{
    public int VideoPort { get; set; } = 9000;
    public int InputPort { get; set; } = 9001;
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 15;

    /// <summary>libx264 (CPU, works everywhere), h264_nvenc (NVIDIA), h264_qsv (Intel), h264_amf (AMD).</summary>
    public string Encoder { get; set; } = "libx264";

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Let ffmpeg's ddagrab filter do the Desktop Duplication capture on the GPU
    /// instead of our C# capturer. Lower CPU use and it draws the mouse cursor.</summary>
    public bool FfmpegCapture { get; set; }

    /// <summary>Inject the Pencil as a plain mouse instead of a Windows Ink pen (no pressure).</summary>
    public bool MouseMode { get; set; }

    public static ServerOptions Parse(string[] args)
    {
        var o = new ServerOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--video-port": o.VideoPort = int.Parse(Next()); break;
                case "--input-port": o.InputPort = int.Parse(Next()); break;
                case "--fps": o.Fps = int.Parse(Next()); break;
                case "--bitrate": o.BitrateMbps = int.Parse(Next()); break;
                case "--encoder": o.Encoder = Next(); break;
                case "--ffmpeg": o.FfmpegPath = Next(); break;
                case "--ffmpeg-capture": o.FfmpegCapture = true; break;
                case "--mouse": o.MouseMode = true; break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return o;
    }
}
