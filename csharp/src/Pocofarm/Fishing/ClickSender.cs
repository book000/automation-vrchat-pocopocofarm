using Pocofarm.Core;
using Pocofarm.Platform;

namespace Pocofarm.Fishing;

/// <summary>クリックの種別(収支の突き合わせとログで使う名前)。</summary>
public static class ClickKind
{
    public const string Cast = "投げ", Reel = "引き", Probe = "当たりなし確認";
}

/// <summary>
/// ゲームへのクリック送信。VRChat が前面でカーソルが内側にあるときだけ送り、種別ごとの送信数を数える。
/// 送れなかったときは一定時間保留する(毎フレーム前面化を試みない)。
/// </summary>
public sealed class ClickSender(Config cfg, Status status, bool dryRun)
{
    readonly Dictionary<string, int> counts = [];
    double blockUntil;

    /// <summary>種別ごとの送信数。</summary>
    public IReadOnlyDictionary<string, int> Counts => counts;

    public int Count(string kind) => counts.GetValueOrDefault(kind);

    (bool Ok, string Reason) CanClick(nint hwnd)
    {
        if (hwnd == 0 || WinApi.FindWindow(cfg.WindowTitle) == 0) return (false, "VRChat ウィンドウなし");
        if (dryRun ? !WinApi.IsForeground(hwnd) : !WinApi.EnsureForeground(hwnd)) return (false, "VRChat を前面にできません");
        if (!WinApi.CursorInside(hwnd)) return (false, "カーソルが VRChat の外です");
        return (true, "");
    }

    /// <summary>クリックを送る。送れなければ false(保留中も false)。dry-run では送ったことにするだけ。</summary>
    public bool Click(nint hwnd, string kind)
    {
        if (EventLog.Now() < blockUntil) return false;
        var (ok, reason) = CanClick(hwnd);
        if (!ok)
        {
            blockUntil = EventLog.Now() + cfg.ClickHoldSeconds;
            status.Set("message", $"クリック保留: {reason}");
            Log.Warn($"クリック保留({kind}): {reason}");
            return false;
        }
        if (dryRun) Log.Info($"[dry-run] クリック({kind})を送信したことにします");
        else WinApi.Click();
        counts[kind] = counts.GetValueOrDefault(kind) + 1;
        return true;
    }
}
