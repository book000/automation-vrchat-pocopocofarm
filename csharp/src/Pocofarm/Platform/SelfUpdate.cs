using Pocofarm.Core;

namespace Pocofarm.Platform;

/// <summary>
/// exe の自動入れ替え。常駐(all)が、exe と同じ場所に置かれた pocofarm.new.exe(または update_source の新しい exe)を見つけたら、
/// 釣りプロセスを止め、起動中の exe を pocofarm.exe.old へ改名(Windows は実行中でも改名できる)して差し替え、新しい常駐を起動する。
/// </summary>
public static class SelfUpdate
{
    const long MinBytes = 10_000_000;     // 途中までのコピーや壊れたファイルを入れ替えない
    static double lastSourceCheck;

    static string NewPath(string self) => Path.Combine(Path.GetDirectoryName(self)!, "pocofarm.new.exe");

    /// <summary>入れ替える新しい exe があればそのパスを返す。update_source が新しければ先に pocofarm.new.exe へ取り込む。</summary>
    public static string? Pending(string self, Config cfg)
    {
        var next = NewPath(self);
        double now = Environment.TickCount64 / 1000.0;
        if (cfg.UpdateSource.Length > 0 && now - lastSourceCheck >= cfg.UpdateCheckSeconds)
        {
            lastSourceCheck = now;
            try
            {
                var src = new FileInfo(cfg.UpdateSource);
                if (src.Exists && src.Length > MinBytes && src.LastWriteTimeUtc > File.GetLastWriteTimeUtc(self).AddSeconds(1) && !File.Exists(next))
                {
                    var tmp = next + ".tmp";
                    File.Copy(src.FullName, tmp, true);
                    File.Move(tmp, next, true);
                    Log.Info($"更新元から新しい exe を取り込みました: {src.FullName}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("更新元の確認に失敗: " + ex.Message); }
        }
        try { return File.Exists(next) && new FileInfo(next).Length > MinBytes ? next : null; }
        catch (IOException) { return null; }
    }

    /// <summary>実行中の exe を新しいものに置き換え、同じ引数で新しい常駐を起動する。失敗したら false(新しい exe は .failed へ退避)。</summary>
    public static bool Swap(string self, string next, string[] args)
    {
        var old = self + ".old";
        // 前回の .old がまだ実行中だと上書きできないので、別名へ退避する
        try { if (File.Exists(old)) File.Delete(old); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { old = $"{self}.{DateTime.Now:yyyyMMddHHmmss}.old"; }
        try
        {
            File.Move(self, old, true);
            File.Move(next, self, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("exe の入れ替えに失敗: " + ex.Message);
            try { if (!File.Exists(self) && File.Exists(old)) File.Move(old, self); File.Move(next, next + ".failed", true); }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
            return false;
        }
        var psi = new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        System.Diagnostics.Process.Start(psi);
        return true;
    }

    /// <summary>入れ替えで残った古い exe(*.old、*.failed、放置された .tmp)を消す。実行中などで消せないものは次回に回す。消した件数を返す。</summary>
    public static int CleanupLeftovers(string self, TimeSpan? staleTmp = null)
    {
        var dir = Path.GetDirectoryName(self)!;
        var name = Path.GetFileName(self);
        var next = Path.GetFileName(NewPath(self));
        var tmpAge = staleTmp ?? TimeSpan.FromHours(1);
        int removed = 0;
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var n = Path.GetFileName(f);
            bool leftover = n.Equals(name + ".old", StringComparison.OrdinalIgnoreCase)
                || (n.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                || n.Equals(next + ".failed", StringComparison.OrdinalIgnoreCase)
                || (n.Equals(next + ".tmp", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > tmpAge);
            if (!leftover) continue;
            try { File.Delete(f); removed++; Log.Info($"古い exe を削除しました: {n}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
