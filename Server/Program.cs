using System.Net.NetworkInformation;
using System.Net.Sockets;
using DrawingServer;

// Physical pixels everywhere: without this, capture size and pen coordinates are scaled by DPI.
Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

ServerOptions options;
try { options = ServerOptions.Parse(args); }
catch (ArgumentException e)
{
    Console.WriteLine(e.Message);
    Console.WriteLine("Usage: DrawingServer [--encoder libx264|h264_nvenc|h264_qsv|h264_amf] [--bitrate 15] [--fps 60]");
    Console.WriteLine("                     [--ffmpeg-capture] [--mouse] [--ffmpeg path] [--video-port 9000] [--input-port 9001]");
    return 1;
}

Console.WriteLine("Drawing server. Enter one of these addresses in the iPad app:");
foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
{
    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
    foreach (var addr in nic.GetIPProperties().UnicastAddresses)
        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
            Console.WriteLine($"  {addr.Address,-15}  ({nic.Name})");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var injector = new InputInjector(options.MouseMode);
var input = new InputServer(options, injector).RunAsync(cts.Token);
var video = new VideoServer(options).RunAsync(cts.Token);

try { await Task.WhenAll(input, video); }
catch (OperationCanceledException) { }
return 0;
