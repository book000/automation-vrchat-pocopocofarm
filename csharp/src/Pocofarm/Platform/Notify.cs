using System.Diagnostics;
using Pocofarm.Core;

namespace Pocofarm.Platform;

/// <summary>Windows トースト通知。PowerShell(WinRT)を呼ぶだけで、失敗しても釣りは止めない。</summary>
public static class Notify
{
    // PowerShell 自身の AppUserModelID。未登録アプリ名だと通知が出ないため借りる
    const string AppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>通知を非同期に出す。</summary>
    public static void Toast(string title, string body)
    {
        var script = BuildScript(title, body);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-NonInteractive", "-Command", script])
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
        }
        catch (Exception ex) { Log.Warn($"トースト通知に失敗: {ex.Message}"); }
    }

    /// <summary>XML と PowerShell の両方でエスケープしたスクリプトを作る(テスト用に公開)。</summary>
    public static string BuildScript(string title, string body)
    {
        string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "''");
        return $"""
            [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime]
            [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime]
            $x = New-Object Windows.Data.Xml.Dom.XmlDocument
            $x.LoadXml('<toast><visual><binding template="ToastGeneric"><text>{E(title)}</text><text>{E(body)}</text></binding></visual></toast>')
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppId}').Show([Windows.UI.Notifications.ToastNotification]::new($x))
            """;
    }
}
