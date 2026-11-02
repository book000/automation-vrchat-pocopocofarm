namespace Pocofarm.Fishing;

/// <summary>釣りと関係のない画面の文字(ネームプレート、農場の操作、お届け通知など)が通知欄に写ったときの種別。OCR の読み違いがあるので一部の文字だけで判定する。</summary>
public static class ToastNoise
{
    static readonly (string Category, string[] Hints)[] Rules =
    [
        ("nameplate", ["AskMe", "MyPro", "AVATAR", "STATUS", "Tomac", "TOmac", "ReV3"]),
        ("camera_ui", ["PHO", "NONE", "NON・"]),
        ("delivery", ["届き", "材料", "かばんの", "かはんの"]),
        ("farm", ["タネ", "コイン", "買いました", "育って", "長押し", "とれた", "出荷箱", "レベル:", "持っているタ"]),
        ("hint_panel", ["釣り竿", "クリックで", "池を見て"]),
    ];

    /// <summary>当てはまる種別。どれでもなければ null(本当の未分類)。</summary>
    public static string? Category(string text)
    {
        foreach (var (category, hints) in Rules)
            if (hints.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase))) return category;
        return null;
    }
}
