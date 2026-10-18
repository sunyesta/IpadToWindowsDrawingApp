using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace DrawingServer;

/// <summary>
/// Captures the primary monitor with the DXGI Desktop Duplication API.
/// Frames stay on the GPU until <see cref="CopyTo"/> is called, so frames we decide
/// to skip (to cap the frame rate) cost almost nothing.
///
/// Create one and keep it for the server's lifetime: a duplication re-created right after the
/// previous one was released often returns a black first frame on a still screen.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutput1 _output;
    private readonly ID3D11Texture2D _staging;
    private IDXGIOutputDuplication? _duplication;
    private bool _needsFirstFrame = true;

    public int Width { get; }
    public int Height { get; }

    /// <summary>The staging texture holds a desktop image (kept between iPad sessions).</summary>
    public bool HasFrame { get; private set; }

    /// <summary>The monitor changed size; this capturer must be replaced.</summary>
    public bool ResolutionChanged { get; private set; }

    public ScreenCapturer()
    {
        // The primary monitor is the output whose desktop rectangle starts at (0,0).
        // On hybrid-GPU laptops it may not live on adapter 0, so search them all.
        using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
        (IDXGIAdapter1 adapter, IDXGIOutput output) = FindPrimaryOutput(factory);

        D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out _device!, out _context!).CheckError();
        adapter.Dispose();

        _output = output.QueryInterface<IDXGIOutput1>();
        output.Dispose();

        var bounds = _output.Description.DesktopCoordinates;
        Width = bounds.Right - bounds.Left;
        Height = bounds.Bottom - bounds.Top;

        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        });

        _duplication = _output.DuplicateOutput(_device);
    }

    private static (IDXGIAdapter1, IDXGIOutput) FindPrimaryOutput(IDXGIFactory1 factory)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            for (uint o = 0; adapter!.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
            {
                var r = output!.Description.DesktopCoordinates;
                if (r.Left == 0 && r.Top == 0) return (adapter, output);
                output.Dispose();
            }
            adapter.Dispose();
        }
        throw new InvalidOperationException("No primary monitor found for Desktop Duplication.");
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the screen to change. Returns true when a
    /// new image was copied into the staging texture. The very first call always returns a frame.
    /// </summary>
    public bool TryAcquire(int timeoutMs)
    {
        if (_duplication == null)
        {
            // Duplication was lost (UAC prompt, mode change, fullscreen app...). Try to recreate it.
            try { _duplication = _output.DuplicateOutput(_device); _needsFirstFrame = true; }
            catch { Thread.Sleep(100); return false; }

            var bounds = _output.Description.DesktopCoordinates;
            if (bounds.Right - bounds.Left != Width || bounds.Bottom - bounds.Top != Height)
            {
                ResolutionChanged = true;
                throw new InvalidOperationException("Display resolution changed; reconnect to continue.");
            }
        }

        var result = _duplication.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo info, out IDXGIResource? resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout) return false;
        if (result == Vortice.DXGI.ResultCode.AccessLost)
        {
            _duplication.Dispose();
            _duplication = null;
            return false;
        }
        result.CheckError();

        try
        {
            // LastPresentTime == 0 means only the mouse moved; the desktop image is unchanged.
            // The first frame after (re)creating the duplication must always be taken, though:
            // on a still screen it also reports 0, and it is the only image we would get.
            if (info.LastPresentTime == 0 && !_needsFirstFrame) return false;

            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_staging, texture);
            _needsFirstFrame = false;
            HasFrame = true;
            return true;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>Copies the last acquired frame into <paramref name="dest"/> as tightly packed BGRA.</summary>
    public unsafe void CopyTo(byte[] dest)
    {
        int rowBytes = Width * 4;
        MappedSubresource map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* src = (byte*)map.DataPointer;
            for (int y = 0; y < Height; y++)
                Marshal.Copy((IntPtr)(src + (long)y * map.RowPitch), dest, y * rowBytes, rowBytes);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        _duplication?.Dispose();
        _staging.Dispose();
        _output.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
