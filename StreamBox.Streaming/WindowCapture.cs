using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using StreamBox.Core;

namespace StreamBox.Streaming;

/// <summary>
/// Captura uma janela específica com Windows.Graphics.Capture. Os quadros chegam em BGRA,
/// vindos de uma textura de staging na GPU, prontos para o encoder.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid ActivationFactoryIid = new("00000035-0000-0000-C000-000000000046");

    private readonly nint _windowHandle;
    private readonly object _gate = new();
    private readonly Stack<byte[]> _buffers = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _rtDevice;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private ID3D11Texture2D? _staging;

    private readonly int _targetFps;
    private readonly long _minFrameInterval;
    private long _nextFrameTick;
    private long _offeredFrames;
    private long _acceptedFrames;
    private readonly long _startedTick;

    private bool _running;
    private bool _disposed;
    private bool _firstFrameLogged;
    private bool _sizeMismatchLogged;

    public WindowCapture(nint windowHandle, int maxHeight, int targetFps = 0)
    {
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("Janela inválida para captura.", nameof(windowHandle));

        _targetFps = Math.Clamp(targetFps, 0, 120);
        _minFrameInterval = _targetFps > 0 ? Stopwatch.Frequency / _targetFps : 0;
        _startedTick = Stopwatch.GetTimestamp();
        _windowHandle = windowHandle;
        _item = CreateItemForWindow(windowHandle);
        _item.Closed += (_, _) => Closed?.Invoke();

        var itemSize = _item.Size;
        var target = CaptureSize.Fit(itemSize.Width, itemSize.Height, maxHeight);
        Width = target.Width;
        Height = target.Height;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            CreateWinRtDevice(),
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            new SizeInt32 { Width = Width, Height = Height });

        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }

        CreateStagingTexture();
        AppLog.Info($"Captura pronta: janela 0x{windowHandle:X} → {Width}x{Height} (origem {itemSize.Width}x{itemSize.Height})");
    }

    public event Action<CapturedFrame>? FrameArrived;

    public event Action? Closed;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Quadros que o Windows entregou (taxa real da janela).</summary>
    public long OfferedFrames => Interlocked.Read(ref _offeredFrames);

    /// <summary>Quadros aceitos após o limitador de taxa.</summary>
    public long AcceptedFrames => Interlocked.Read(ref _acceptedFrames);

    public double SecondsSinceStart =>
        Math.Max(0.001, (Stopwatch.GetTimestamp() - _startedTick) / (double)Stopwatch.Frequency);

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public void Start()
    {
        if (_running || _disposed) return;

        _framePool!.FrameArrived += OnFrameArrived;
        _session!.StartCapture();
        _running = true;
    }

    private IDirect3DDevice CreateWinRtDevice()
    {
        var flags = D3D11CreateDeviceBgraSupport;
        var featureLevels = new[] { FeatureLevel11_1, FeatureLevel11_0, FeatureLevel10_1, FeatureLevel10_0 };

        var result = D3D11CreateDevice(
            IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, flags,
            featureLevels, (uint)featureLevels.Length, SdkVersion,
            out var device, out _, out var context);

        if (result < 0)
        {
            AppLog.Warn($"GPU indisponível (0x{result:X8}); usando rasterizador por software.");
            result = D3D11CreateDevice(
                IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, flags,
                featureLevels, (uint)featureLevels.Length, SdkVersion,
                out device, out _, out context);
        }

        Marshal.ThrowExceptionForHR(result);

        _device = new ID3D11Device(device);
        _context = new ID3D11DeviceContext(context);

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var rtDevicePointer);
        Marshal.ThrowExceptionForHR(hr);

        _rtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(rtDevicePointer);
        WinRtInterop.Release(rtDevicePointer);
        return _rtDevice;
    }

    private void CreateStagingTexture()
    {
        _staging?.Dispose();
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private unsafe void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_running || _disposed) return;

        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame is null) return;

            Interlocked.Increment(ref _offeredFrames);

            // O Windows entrega quadros na taxa da tela; respeitamos o alvo para não desperdiçar
            // GPU. A comparação é por agenda (e não por intervalo desde o último aceito) porque
            // com jitter de 1–2 ms o teste de intervalo rejeitava quadros de uma fonte de 30 FPS
            // em sequência, derrubando a taxa real para ~26 FPS.
            if (_minFrameInterval > 0)
            {
                var now = Stopwatch.GetTimestamp();
                if (now < _nextFrameTick) return;

                _nextFrameTick += _minFrameInterval;
                if (_nextFrameTick < now) _nextFrameTick = now + _minFrameInterval;
            }

            Interlocked.Increment(ref _acceptedFrames);

            var surfacePointer = WinRtInterop.FromInspectable(frame.Surface);
            IDirect3DDxgiInterfaceAccess access;
            try
            {
                access = WinRtInterop.Wrap<IDirect3DDxgiInterfaceAccess>(surfacePointer);
            }
            finally
            {
                WinRtInterop.Release(surfacePointer);
            }

            var textureIid = Texture2DIid;
            var texturePointer = access.GetInterface(ref textureIid);

            using var texture = new ID3D11Texture2D(texturePointer);

            // O descritor da textura é a fonte da verdade (ContentSize devolve o tamanho da fonte,
            // não o tamanho do frame pool usado para escalar).
            var description = texture.Description;
            if ((int)description.Width != Width || (int)description.Height != Height)
            {
                LogSizeMismatch((int)description.Width, (int)description.Height);
                return;
            }

            _context!.CopyResource(_staging!, texture);

            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowBytes = Width * 4;
                var buffer = RentBuffer(rowBytes * Height);
                var source = (byte*)mapped.DataPointer;

                for (var y = 0; y < Height; y++)
                    Marshal.Copy((IntPtr)(source + (y * mapped.RowPitch)), buffer, y * rowBytes, rowBytes);

                var captured = new CapturedFrame(
                    buffer, rowBytes * Height, Width, Height, Stopwatch.GetTimestamp(), ReturnBuffer);

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    AppLog.Info($"Primeiro quadro capturado: {Width}x{Height}");
                }

                FrameArrived?.Invoke(captured);
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Falha ao processar quadro capturado: {ex.Message}");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void LogSizeMismatch(int width, int height)
    {
        if (_sizeMismatchLogged) return;
        _sizeMismatchLogged = true;
        AppLog.Warn($"Textura de captura em {width}x{height}, esperado {Width}x{Height}; quadros ignorados.");
    }

    private byte[] RentBuffer(int size)
    {
        lock (_gate)
        {
            while (_buffers.Count > 0)
            {
                var candidate = _buffers.Pop();
                if (candidate.Length == size) return candidate;
            }
        }

        return new byte[size];
    }

    private void ReturnBuffer(CapturedFrame frame)
    {
        lock (_gate)
        {
            if (_buffers.Count < 4) _buffers.Push(frame.Buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;

        try { if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived; } catch { }
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        try { _staging?.Dispose(); } catch { }
        try { _rtDevice?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }

        _session = null;
        _framePool = null;
        _staging = null;
        _rtDevice = null;
        _context = null;
        _device = null;
    }

    // --------------------------------------------------------- interop COM

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var factoryPointer = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem", ActivationFactoryIid);

        IGraphicsCaptureItemInterop interop;
        try
        {
            interop = WinRtInterop.Wrap<IGraphicsCaptureItemInterop>(factoryPointer);
        }
        finally
        {
            WinRtInterop.Release(factoryPointer);
        }

        var itemIid = GraphicsCaptureItemIid;
        var itemPointer = interop.CreateForWindow(hwnd, ref itemIid);
        if (itemPointer == IntPtr.Zero)
            throw new InvalidOperationException("Não foi possível capturar esta janela.");

        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            WinRtInterop.Release(itemPointer);
        }
    }

    private static IntPtr GetActivationFactory(string className, Guid iid)
    {
        WindowsCreateString(className, className.Length, out var hstring);
        try
        {
            RoGetActivationFactory(hstring, ref iid, out var factory);
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        int[] featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const int DriverTypeHardware = 1;
    private const int DriverTypeWarp = 5;
    private const uint SdkVersion = 7;
    private const int FeatureLevel11_1 = 0xB100;
    private const int FeatureLevel11_0 = 0xB000;
    private const int FeatureLevel10_1 = 0xA100;
    private const int FeatureLevel10_0 = 0xA000;

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] nint window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
