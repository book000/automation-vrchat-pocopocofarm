using System.Diagnostics;

namespace Pocofarm.Core;

/// <summary>Windows のトースト通知を出す(外部サービスは使わない)。連続して出さないよう間隔を空ける。</summary>
public static class DesktopNotice
{
    const double MinGapSeconds = 600;
    static double lastAt;

    /// <summary>通知を出す。直近 10 分に出していれば何もしない。失敗しても釣りは止めない。</summary>
    public static void Show(string title, string body)
    {
        double now = EventLog.Now();
        if (now - lastAt < MinGapSeconds) return;
        lastAt = now;
        try
        {
            static string Esc(string s) => System.Security.SecurityElement.Escape(s).Replace("'", "''");
            var script = "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null;" +
                "$x = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
                $"$x.GetElementsByTagName('text').Item(0).AppendChild($x.CreateTextNode('{Esc(title)}')) | Out-Null;" +
                $"$x.GetElementsByTagName('text').Item(1).AppendChild($x.CreateTextNode('{Esc(body)}')) | Out-Null;" +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe').Show([Windows.UI.Notifications.ToastNotification]::new($x))";
            using var _ = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script]) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { Log.Warn($"通知を出せませんでした: {e.Message}"); }
    }
}
