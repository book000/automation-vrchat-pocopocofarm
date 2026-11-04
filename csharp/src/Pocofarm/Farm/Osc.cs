using System.Net.Sockets;
using System.Text;

namespace Pocofarm.Farm;

/// <summary>VRChat の OSC 入力(UDP)へ最小限のメッセージを送る。VRChat 側で OSC を有効にしておく必要がある。</summary>
public static class Osc
{
    static void Pad(List<byte> b, string s)
    {
        b.AddRange(Encoding.ASCII.GetBytes(s));
        b.Add(0);
        while (b.Count % 4 != 0) b.Add(0);
    }

    /// <summary>引数は int・bool・float の配列に対応。</summary>
    static byte[] Message(string address, object arg)
    {
        var b = new List<byte>();
        Pad(b, address);
        Pad(b, arg switch { int => ",i", true => ",T", float[] f => "," + new string('f', f.Length), _ => ",F" });
        if (arg is int i) b.AddRange(new[] { (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i });
        if (arg is float[] fs) foreach (var x in fs) b.AddRange(BitConverter.GetBytes(x).Reverse());
        return [.. b];
    }

    /// <summary>カメラの位置と向き(x, y, z, pitch, yaw, roll)を設定する。カメラが開いている間だけ効く。</summary>
    public static void SetPose(float[] pose, int port)
    {
        using var udp = new UdpClient();
        var m = Message("/usercamera/Pose", pose);
        udp.Send(m, m.Length, "127.0.0.1", port);
    }

    /// <summary>
    /// Spout 配信の入り切り。実測では /usercamera/* はカメラを手で開いている間だけ効き(Mode でカメラを開くことはできない)、
    /// Streaming は bool(int は無視される)。
    /// </summary>
    public static void SetStreaming(bool on, int port)
    {
        using var udp = new UdpClient();
        var m = Message("/usercamera/Streaming", on);
        udp.Send(m, m.Length, "127.0.0.1", port);
    }

    /// <summary>カメラの画角(Zoom。値が大きいほど広く写る)。カメラが開いている間だけ効く。</summary>
    public static void SetZoom(float zoom, int port)
    {
        using var udp = new UdpClient();
        var m = Message("/usercamera/Zoom", new float[] { zoom });
        udp.Send(m, m.Length, "127.0.0.1", port);
    }

    /// <summary>
    /// /input/UseRight を押し、holdMs 後に離す(デスクトップのクリック相当。ウィンドウのフォーカスに依存しない)。
    /// 離すのは別スレッドで待つので、呼び出しは待たせない。
    /// </summary>
    public static void Use(int holdMs, int port)
    {
        var udp = new UdpClient();
        var down = Message("/input/UseRight", 1);
        udp.Send(down, down.Length, "127.0.0.1", port);
        Task.Delay(holdMs).ContinueWith(_ =>
        {
            var up = Message("/input/UseRight", 0);
            udp.Send(up, up.Length, "127.0.0.1", port);
            udp.Dispose();
        });
    }

    /// <summary>カメラのモード(0=閉じる)。開いている間だけ効く。</summary>
    public static void SetMode(int mode, int port)
    {
        using var udp = new UdpClient();
        var m = Message("/usercamera/Mode", mode);
        udp.Send(m, m.Length, "127.0.0.1", port);
    }
}
