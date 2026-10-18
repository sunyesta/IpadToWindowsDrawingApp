using System.Net;
using System.Net.Sockets;

namespace DrawingServer;

/// <summary>Where the next iPad connection for a channel comes from.</summary>
public abstract class ConnectionSource(string channel)
{
    protected string Channel { get; } = channel;

    public abstract Task<Socket> NextAsync(CancellationToken ct);

    public static ConnectionSource Create(ServerOptions options, string channel, int port) =>
        options.Usb ? new UsbConnectionSource(channel, port) : new TcpConnectionSource(channel, port);
}

/// <summary>Wi-Fi / Ethernet: the iPad dials us.</summary>
public sealed class TcpConnectionSource(string channel, int port) : ConnectionSource(channel)
{
    private TcpListener? _listener;

    public override async Task<Socket> NextAsync(CancellationToken ct)
    {
        if (_listener == null)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            Console.WriteLine($"{Channel}: listening on TCP {port}");
        }
        Socket socket = await _listener.AcceptSocketAsync(ct);
        Console.WriteLine($"{Channel}: iPad connected from {socket.RemoteEndPoint}");
        return socket;
    }
}

/// <summary>USB cable: we dial the iPad through usbmuxd, retrying until the app is waiting for us.</summary>
public sealed class UsbConnectionSource(string channel, int port) : ConnectionSource(channel)
{
    public override async Task<Socket> NextAsync(CancellationToken ct)
    {
        string? lastProblem = null;
        while (true)
        {
            try
            {
                Socket socket = await UsbMux.ConnectAsync(port, ct);
                Console.WriteLine($"{Channel}: iPad connected over USB");
                return socket;
            }
            catch (Exception e) when (e is UsbMux.UsbMuxException or SocketException or IOException)
            {
                if (e.Message != lastProblem) Console.WriteLine($"{Channel}: waiting for the iPad ({e.Message})");
                lastProblem = e.Message;
            }
            await Task.Delay(1000, ct);
        }
    }
}
