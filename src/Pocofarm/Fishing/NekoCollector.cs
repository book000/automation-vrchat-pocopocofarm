using Pocofarm.Core;

namespace Pocofarm.Fishing;

/// <summary>ねこちゃんの通知を 1 回の表示につき 1 件にまとめる。OCR は表示中に何度も読むので、いちばん長く読めた本文を残す。</summary>
public sealed class NekoCollector(Config cfg, EventLog events, EvidenceSaver saveEvidence)
{
    sealed record NekoNote(string Text, double FirstT, double LastT, string Evidence, string Engine);

    NekoNote? neko;

    public void Note(Sighting s, Frame f, string state)
    {
        var text = s.Neko!;
        if (neko is { } cur && s.T - cur.LastT < cfg.NekoMergeGap && Toast.SeqRatio(cur.Text, text) >= 0.5)
        {
            neko = cur with { Text = text.Length > cur.Text.Length ? text : cur.Text, LastT = s.T };
            return;
        }
        Flush(state, force: true);
        neko = new NekoNote(text, s.T, s.T, saveEvidence(f, "neko", new { text, engine = s.Engine, state }), s.Engine);
    }

    /// <summary>表示が途切れた(または force)ら、まとめた通知をイベントに書く。</summary>
    public void Flush(string state, bool force = false)
    {
        if (neko is not { } n || (!force && EventLog.Now() - n.LastT < cfg.NekoFlushSeconds)) return;
        neko = null;
        events.Write("neko", ("text", n.Text), ("state", state), ("engine", n.Engine), ("evidence", n.Evidence));
        Log.Info($"ねこちゃんの通知: {n.Text}");
    }
}

/// <summary>画面の証拠を保存してファイル名を返す関数。</summary>
public delegate string EvidenceSaver(Frame frame, string name, object meta);
