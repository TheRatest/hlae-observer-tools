using System;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace HlaeObsTools.Services.Graphics;

public sealed class D3D11DeviceService : IDisposable
{
    public static D3D11DeviceService Instance { get; } = new();

    private ID3D11Device? _device;
    private ID3D11Device1? _device1;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;

    public ID3D11Device Device => _device ?? throw new InvalidOperationException("D3D11 device not initialized.");
    public ID3D11Device1? Device1 => _device1;
    public ID3D11DeviceContext Context => _context ?? throw new InvalidOperationException("D3D11 context not initialized.");
    public IDXGIFactory2 Factory => _factory ?? throw new InvalidOperationException("DXGI factory not initialized.");
    public object ContextLock { get; } = new();
    public bool IsReady => _device != null && _context != null && _factory != null;

    private D3D11DeviceService()
    {
        CreateDevice();
    }

    private void CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            flags,
            levels,
            out _device,
            out _,
            out _context);

        if (result.Failure)
        {
            result = D3D11CreateDevice(
                null,
                DriverType.Warp,
                flags,
                levels,
                out _device,
                out _,
                out _context);
        }

        if (result.Failure || _device == null || _context == null)
            throw new InvalidOperationException($"Failed to create shared D3D11 device: 0x{result.Code:X8}");

        _device1 = _device.QueryInterfaceOrNull<ID3D11Device1>();
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        try
        {
            using var multithread = _context.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);
        }
        catch
        {
            // Ignore if multithread interface isn't available.
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
        _device1?.Dispose();
        _device1 = null;
        _device?.Dispose();
        _device = null;
        _factory?.Dispose();
        _factory = null;
    }
}
