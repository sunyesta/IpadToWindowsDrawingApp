using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DrawingServer;

/// <summary>
/// Minimal client for usbmuxd, Apple's USB multiplexer. It comes with Apple Mobile Device Support
/// (installed by the Apple Devices app or iTunes) and listens on 127.0.0.1:27015 on Windows
/// (/var/run/usbmuxd on macOS). A "Connect" request turns the socket into a plain TCP tunnel to a
/// port on the iPad, which is how Duet/Astropad-style wired modes work.
///
/// Each request is [u32 total length][u32 version = 1][u32 type = 8 (plist)][u32 tag], little-endian,
/// followed by an XML property list.
/// </summary>
public static class UsbMux
{
    public sealed class UsbMuxException : Exception
    {
        public UsbMuxException(string message) : base(message) { }
    }

    private static int _tag;

    /// <summary>Opens a tunnel to <paramref name="port"/> on the first iPad connected over USB.</summary>
    public static async Task<Socket> ConnectAsync(int port, CancellationToken ct)
    {
        long deviceId;
        using (var control = await OpenAsync(ct))
        {
            var devices = await RequestAsync(control, "ListDevices", new Dictionary<string, object>(), ct);
            deviceId = FindUsbDevice(devices) ?? throw new UsbMuxException("no iPad connected over USB");
        }

        var socket = await OpenAsync(ct);
        try
        {
            var reply = await RequestAsync(socket, "Connect", new Dictionary<string, object>
            {
                ["DeviceID"] = deviceId,
                ["PortNumber"] = (long)BinaryPrimitives.ReverseEndianness((ushort)port),  // network byte order
            }, ct);
            long result = reply.TryGetValue("Number", out var number) ? Convert.ToInt64(number) : -1;
            if (result != 0)
                throw new UsbMuxException(result == 3 ? "the iPad app isn't waiting for USB; open it and pick USB" : $"usbmuxd error {result}");
            return socket;  // from here on it's a raw TCP stream to the iPad
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<Socket> OpenAsync(CancellationToken ct)
    {
        Socket socket;
        EndPoint endpoint;
        if (OperatingSystem.IsWindows())
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            endpoint = new IPEndPoint(IPAddress.Loopback, 27015);
        }
        else
        {
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            endpoint = new UnixDomainSocketEndPoint("/var/run/usbmuxd");
        }

        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return socket;
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new UsbMuxException($"can't reach Apple's USB service ({e.Message}); install the Apple Devices app or iTunes");
        }
    }

    private static async Task<Dictionary<string, object>> RequestAsync(Socket socket, string messageType,
        Dictionary<string, object> fields, CancellationToken ct)
    {
        var request = new Dictionary<string, object>(fields)
        {
            ["MessageType"] = messageType,
            ["ClientVersionString"] = "DrawingServer",
            ["ProgName"] = "DrawingServer",
        };
        byte[] body = Encoding.UTF8.GetBytes(Plist.Write(request));

        var message = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)message.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), (uint)Interlocked.Increment(ref _tag));
        body.CopyTo(message, 16);
        await socket.SendAsync(message, SocketFlags.None, ct);

        var header = new byte[16];
        await ReceiveExactlyAsync(socket, header, ct);
        var reply = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header) - 16];
        await ReceiveExactlyAsync(socket, reply, ct);
        return Plist.ReadDictionary(Encoding.UTF8.GetString(reply));
    }

    private static async Task ReceiveExactlyAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        for (int read = 0; read < buffer.Length;)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct);
            if (n == 0) throw new UsbMuxException("Apple's USB service closed the connection");
            read += n;
        }
    }

    private static long? FindUsbDevice(Dictionary<string, object> reply)
    {
        if (!reply.TryGetValue("DeviceList", out var list) || list is not List<object> devices) return null;
        foreach (var device in devices.OfType<Dictionary<string, object>>())
        {
            // Skip devices macOS/iTunes reaches over Wi-Fi sync ("Network"): only the cable is fast.
            if (device.TryGetValue("Properties", out var p) && p is Dictionary<string, object> props &&
                props.TryGetValue("ConnectionType", out var type) && type as string == "USB" &&
                props.TryGetValue("DeviceID", out var id))
                return Convert.ToInt64(id);
        }
        return null;
    }

    /// <summary>Just enough XML property list support for usbmuxd messages.</summary>
    private static class Plist
    {
        public static string Write(Dictionary<string, object> dict)
        {
            var doc = new XDocument(
                new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
                new XElement("plist", new XAttribute("version", "1.0"), WriteValue(dict)));
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + doc;
        }

        private static XElement WriteValue(object value) => value switch
        {
            string s => new XElement("string", s),
            long or int => new XElement("integer", Convert.ToInt64(value)),
            bool b => new XElement(b ? "true" : "false"),
            Dictionary<string, object> d => new XElement("dict",
                d.SelectMany(kv => new[] { new XElement("key", kv.Key), WriteValue(kv.Value) })),
            _ => throw new ArgumentException($"unsupported plist value {value.GetType()}"),
        };

        public static Dictionary<string, object> ReadDictionary(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var root = XDocument.Load(reader).Root?.Elements().FirstOrDefault();
            return root != null && ReadValue(root) is Dictionary<string, object> dict ? dict : new Dictionary<string, object>();
        }

        private static object ReadValue(XElement e)
        {
            switch (e.Name.LocalName)
            {
                case "dict":
                    var dict = new Dictionary<string, object>();
                    var children = e.Elements().ToList();
                    for (int i = 0; i + 1 < children.Count; i += 2)
                        dict[children[i].Value] = ReadValue(children[i + 1]);
                    return dict;
                case "array": return e.Elements().Select(ReadValue).ToList();
                case "integer": return long.Parse(e.Value);
                case "true": return true;
                case "false": return false;
                case "data": return Convert.FromBase64String(e.Value);
                default: return e.Value;  // string, real, date
            }
        }
    }
}
