using System.Runtime.InteropServices;
using Pocofarm.Core;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Pocofarm.Platform;

/// <summary>
/// Windows.Graphics.Capture による VRChat ウィンドウのキャプチャ。クライアント領域だけを BGRA で保持する。
/// ウィンドウが他に隠れても撮れ、モニター間移動やリサイズにもフレーム側で追従する。
/// </summary>
public sealed unsafe class WgcCapture : IDisposable
{
    static readonly Guid IidCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IidInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IidDxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("combase.dll")] static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string src, int len, out nint hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(nint hstring);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    readonly nint hwnd;
    readonly object gate = new();
    ID3D11Device? device;
    ID3D11DeviceContext? context;
    ID3D11Texture2D? staging;
    IDirect3DDevice? winrtDevice;
    GraphicsCaptureItem? item;
    Direct3D11CaptureFramePool? pool;
    GraphicsCaptureSession? session;
    Frame? shared;        // キャプチャスレッドが書く共有バッファ。読み手は CopyLatestTo で自前の Frame に写す
    double latestAt;
    long frames;
    readonly AutoResetEvent arrived = new(false);

    public WgcCapture(nint hwnd) { this.hwnd = hwnd; }

    /// <summary>最新フレームの到着時刻(epoch 秒)。</summary>
    public double LatestAt { get { lock (gate) return latestAt; } }

    /// <summary>受信したフレーム数。</summary>
    public long FrameCount { get { lock (gate) return frames; } }

    /// <summary>新しいフレームが届くか timeoutMs 経つまで待つ(ビジーループで CPU を食わないため)。</summary>
    public void WaitFrame(int timeoutMs) => arrived.WaitOne(timeoutMs);

    /// <summary>キャプチャを開始する。失敗時は例外。</summary>
    public void Start()
    {
        Stop();
        item = CreateItem(hwnd);
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out device, out context).CheckError();
        using (var dxgi = device!.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var p));
            winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(p);
            Marshal.Release(p);
        }
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        pool.FrameArrived += OnFrame;
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        // 黄色い取得枠を消す(Windows 11 以降。使えない環境では枠が出るだけなので続行する)
        try
        {
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask().GetAwaiter().GetResult();
            session.IsBorderRequired = false;
        }
        catch (Exception ex) { Log.Warn($"キャプチャ枠を消せません: {ex.Message}"); }
        session.StartCapture();
    }

    /// <summary>キャプチャを止めて解放する。</summary>
    public void Stop()
    {
        if (pool is not null) pool.FrameArrived -= OnFrame;
        session?.Dispose(); pool?.Dispose();
        staging?.Dispose(); context?.Dispose(); device?.Dispose();
        session = null; pool = null; staging = null; context = null; device = null; item = null;
        lock (gate) { shared = null; latestAt = 0; }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 最新フレームを dest に写す(大きさが違えば作り直す)。seen より新しい版があれば true、無ければ false(dest は触らない)。
    /// フレームを毎回確保すると LOH を食い潰すので、読み手のバッファを使い回す。
    /// </summary>
    public bool CopyLatestTo(ref Frame? dest, ref long seen)
    {
        lock (gate)
        {
            if (shared is null || frames == seen) return false;
            if (dest is null || dest.Width != shared.Width || dest.Height != shared.Height) dest = new Frame(shared.Width, shared.Height);
            Buffer.BlockCopy(shared.Data, 0, dest.Data, 0, shared.Data.Length);
            seen = frames;
            return true;
        }
    }

    static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", 44, out var cls));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(cls, IidInterop, out var factory));
            try
            {
                // IGraphicsCaptureItemInterop は IUnknown 直系なので CreateForWindow は 4 番目
                var fn = (delegate* unmanaged[Stdcall]<nint, nint, in Guid, out nint, int>)(*(nint**)factory)[3];
                Marshal.ThrowExceptionForHR(fn(factory, hwnd, IidCaptureItem, out var itemPtr));
                try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { _ = WindowsDeleteString(cls); }
    }

    void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using var f = sender.TryGetNextFrame();
            if (f is null || device is null || context is null) return;
            var size = f.ContentSize;
            var surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(f.Surface);
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, IidDxgiAccess, out var access));
                try
                {
                    var get = (delegate* unmanaged[Stdcall]<nint, in Guid, out nint, int>)(*(nint**)access)[3];
                    Marshal.ThrowExceptionForHR(get(access, IidTexture2D, out var texPtr));
                    using var tex = new ID3D11Texture2D(texPtr);
                    Store(tex);
                }
                finally { Marshal.Release(access); }
            }
            finally { Marshal.Release(surfacePtr); }
            if (size.Width != item!.Size.Width || size.Height != item.Size.Height)
                sender.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        }
        catch (Exception)
        {
            // 停止処理との競合など。次のフレームで回復するので握りつぶす
        }
    }

    void Store(ID3D11Texture2D tex)
    {
        var desc = tex.Description;
        if (staging is null || staging.Description.Width != desc.Width || staging.Description.Height != desc.Height)
        {
            staging?.Dispose();
            staging = device!.CreateTexture2D(new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
        context!.CopyResource(staging, tex);
        var map = context.Map(staging, 0, MapMode.Read);
        try
        {
            // クライアント領域をフレーム内の位置に直して切り出す
            var geo = WinApi.Geometry(hwnd);
            var fb = WinApi.FrameBounds(hwnd);
            int ox = fb is { } b ? geo.Left - b.X : 0, oy = fb is { } b2 ? geo.Top - b2.Y : 0;
            ox = Math.Clamp(ox, 0, (int)desc.Width); oy = Math.Clamp(oy, 0, (int)desc.Height);
            int w = Math.Min(geo.Width, (int)desc.Width - ox), h = Math.Min(geo.Height, (int)desc.Height - oy);
            if (w <= 0 || h <= 0) return;
            lock (gate)
            {
                if (shared is null || shared.Width != w || shared.Height != h) shared = new Frame(w, h);
                for (int y = 0; y < h; y++)
                    Marshal.Copy(map.DataPointer + (oy + y) * (int)map.RowPitch + ox * 4, shared.Data, y * w * 4, w * 4);
                latestAt = EventLog.Now();
                frames++;
            }
            arrived.Set();
        }
        finally { context.Unmap(staging, 0); }
    }
}
