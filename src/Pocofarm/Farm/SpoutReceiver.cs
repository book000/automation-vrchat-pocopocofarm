using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Pocofarm.Core;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Pocofarm.Farm;

/// <summary>
/// Spout 2.007 の受信側。送信元(VRChat のカメラなど)が共有メモリに登録した名前と共有テクスチャのハンドルから、
/// D3D11 で共有テクスチャを開いて CPU に読み出す。ネイティブの Spout DLL は使わない。
/// </summary>
public sealed class SpoutReceiver : IDisposable
{
    const string RegistryName = "SpoutSenderNames";
    const int NameSlot = 256;

    ID3D11Device? device;
    ID3D11DeviceContext? context;
    ID3D11Texture2D? shared, staging;
    uint sharedHandle;
    string? openedName;
    Frame? reuse;

    public string? Sender { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>登録されている送信元の名前一覧。</summary>
    public static List<string> ListSenders()
    {
        var list = new List<string>();
        try
        {
            using var mm = MemoryMappedFile.OpenExisting(RegistryName, MemoryMappedFileRights.Read);
            using var v = mm.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var buf = new byte[v.Capacity];
            v.ReadArray(0, buf, 0, buf.Length);
            for (int o = 0; o + NameSlot <= buf.Length; o += NameSlot)
            {
                int n = Array.IndexOf(buf, (byte)0, o, NameSlot) is var z and >= 0 ? z - o : NameSlot;
                if (n > 0) list.Add(Encoding.ASCII.GetString(buf, o, n));
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>送信元の共有テクスチャ情報。先頭から 共有ハンドル・幅・高さ・DXGI 形式 の順(VRChat 同梱の Spout で実測)。</summary>
    static (int W, int H, uint Handle, uint Format)? ReadInfo(string name)
    {
        try
        {
            using var mm = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            using var v = mm.CreateViewAccessor(0, 16, MemoryMappedFileAccess.Read);
            return ((int)v.ReadUInt32(4), (int)v.ReadUInt32(8), v.ReadUInt32(0), v.ReadUInt32(12));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>名前が空なら VRChat の送信元(VRC で始まる)を優先し、無ければ先頭の送信元を使う。</summary>
    public static string? Pick(string preferred)
    {
        var all = ListSenders();
        if (preferred.Length > 0) return all.Contains(preferred) ? preferred : null;
        return all.FirstOrDefault(n => n.StartsWith("VRC", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
    }

    /// <summary>1 フレーム読む。送信元が無い・形式が未対応のときは null と理由を返す。</summary>
    public Frame? Grab(string preferred, out string? error)
    {
        error = null;
        var name = Pick(preferred);
        if (name is null) { error = preferred.Length > 0 ? $"Spout の送信元「{preferred}」がありません" : "Spout の送信元が見つかりません(VRChat のカメラで Spout を有効にしてください)"; Release(); return null; }
        var info = ReadInfo(name);
        if (info is not { W: > 0, H: > 0, Handle: not 0 } i) { error = $"送信元「{name}」の情報を読めません"; Release(); return null; }
        try
        {
            if (shared is null || openedName != name || sharedHandle != i.Handle || Width != i.W || Height != i.H) Open(name, i.W, i.H, i.Handle);
            var desc = shared!.Description;
            bool bgra = desc.Format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8X8_UNorm;
            bool rgba = desc.Format is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb;
            if (!bgra && !rgba) { error = $"未対応の形式です({desc.Format})"; return null; }
            if (staging is null || staging.Description.Width != desc.Width || staging.Description.Height != desc.Height || staging.Description.Format != desc.Format)
            {
                staging?.Dispose();
                staging = device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
            }
            context!.CopyResource(staging, shared);
            var map = context.Map(staging, 0, MapMode.Read);
            try
            {
                int w = (int)desc.Width, h = (int)desc.Height;
                // 4K のフレームは 33MB で LOH を毎回使うと釣り側の GC 停止を招くため、同じ大きさなら使い回す(呼び出し側は 1 スレッドずつ)
                var f = reuse is { } r && r.Width == w && r.Height == h ? r : (reuse = new Frame(w, h));
                for (int y = 0; y < h; y++) Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, f.Data, y * w * 4, w * 4);
                if (rgba) for (int p = 0; p < f.Data.Length; p += 4) (f.Data[p], f.Data[p + 2]) = (f.Data[p + 2], f.Data[p]);
                for (int p = 3; p < f.Data.Length; p += 4) f.Data[p] = 255;
                return f;
            }
            finally { context.Unmap(staging, 0); }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or COMException)
        {
            error = "共有テクスチャを開けません: " + ex.Message;
            Release();
            return null;
        }
    }

    void Open(string name, int w, int h, uint handle)
    {
        Release();
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out device, out context).CheckError();
        shared = device!.OpenSharedResource<ID3D11Texture2D>((IntPtr)handle);
        openedName = Sender = name; Width = w; Height = h; sharedHandle = handle;
    }

    void Release()
    {
        staging?.Dispose(); staging = null;
        shared?.Dispose(); shared = null;
        context?.Dispose(); context = null;
        device?.Dispose(); device = null;
        openedName = Sender = null; sharedHandle = 0;
    }

    public void Dispose() => Release();
}
