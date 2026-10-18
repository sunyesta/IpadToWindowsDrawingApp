using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DrawingServer;

/// <summary>
/// Receives Pencil / gesture / hotkey events from the iPad on its own TCP socket, so input is
/// never stuck behind a large video frame, and replays them with <see cref="InputInjector"/>.
/// </summary>
public sealed class InputServer(ServerOptions options, InputInjector injector)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, options.InputPort);
        listener.Start();
        Console.WriteLine($"Input: listening on TCP {options.InputPort}");

        while (!ct.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(ct);
            client.NoDelay = true;
            Console.WriteLine($"Input: iPad connected from {client.Client.RemoteEndPoint}");
            try
            {
                await ReceiveAsync(client.GetStream(), ct);
            }
            catch (Exception e) when (e is IOException or SocketException or EndOfStreamException or OperationCanceledException)
            {
                Console.WriteLine($"Input: session ended ({e.Message})");
            }
            finally
            {
                injector.ReleaseAll();   // never leave the pen or middle button "stuck down"
            }
        }
    }

    private async Task ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[2];
        var payload = new byte[255];
        while (!ct.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, ct);
            int length = header[1];
            await stream.ReadExactlyAsync(payload.AsMemory(0, length), ct);
            Dispatch(header[0], payload.AsSpan(0, length));
        }
    }

    private void Dispatch(byte type, ReadOnlySpan<byte> p)
    {
        switch (type)
        {
            case Protocol.InputPen when p.Length >= 22:
                injector.Pen((PenPhase)p[0], F(p, 2), F(p, 6), F(p, 10), F(p, 14), F(p, 18));
                break;
            case Protocol.InputKey when p.Length >= 4:
                injector.Key(BinaryPrimitives.ReadUInt16LittleEndian(p), (KeyModifiers)p[2], (KeyAction)p[3]);
                break;
            case Protocol.InputScroll when p.Length >= 11:
                injector.Scroll(F(p, 0), F(p, 4), BinaryPrimitives.ReadInt16LittleEndian(p[8..]), (KeyModifiers)p[10]);
                break;
            case Protocol.InputMiddleDrag when p.Length >= 9:
                injector.MiddleDrag((DragPhase)p[0], F(p, 1), F(p, 5));
                break;
            default:
                Console.WriteLine($"Input: ignoring message type {type} ({p.Length} bytes)");
                break;
        }
    }

    private static float F(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadSingleLittleEndian(p[offset..]);
}
