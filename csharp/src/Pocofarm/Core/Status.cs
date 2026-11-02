using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pocofarm.Core;

/// <summary>ダッシュボードへ渡す実行状態(status.json)。値を持ち、一定間隔でファイルへ原子的に書き出す。</summary>
public sealed class Status(string path)
{
    static readonly JsonSerializerOptions Opt = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly object gate = new();
    double last;

    /// <summary>現在の状態。Fisher からの読み出しは Get を使う。</summary>
    public Dictionary<string, object?> Data { get; } = new()
    {
        ["pid"] = Environment.ProcessId, ["started"] = EventLog.Now(), ["state"] = "starting", ["message"] = "",
    };

    /// <summary>値を更新する。</summary>
    public void Set(string key, object? value) { lock (gate) Data[key] = value; }

    /// <summary>値を読む。</summary>
    public object? Get(string key) { lock (gate) return Data.GetValueOrDefault(key); }

    /// <summary>0.5秒に1回までファイルへ書く。</summary>
    public void Flush(bool force = false)
    {
        double now = EventLog.Now();
        if (!force && now - last < 0.5) return;
        last = now;
        byte[] bytes;
        lock (gate)
        {
            Data["heartbeat"] = now;
            bytes = JsonSerializer.SerializeToUtf8Bytes(Data, Opt);
        }
        AtomicFile.Write(path, bytes);
    }
}
