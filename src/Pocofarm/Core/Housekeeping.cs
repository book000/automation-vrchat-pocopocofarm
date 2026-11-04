using System.Globalization;

namespace Pocofarm.Core;

/// <summary>起動時と cleanup コマンドで行う掃除。</summary>
public static class Housekeeping
{
    /// <summary>
    /// data/archive の events_*.jsonl のうち keepDays 日より古いものを削除して件数を返す。keepDays が 0 以下なら何もしない。
    /// 古さはファイル名の日時(events_yyyyMMdd_HHmmss)で判定し、読めなければ更新時刻を使う。
    /// </summary>
    public static int PruneArchive(string archiveDir, int keepDays, DateTime now)
    {
        if (keepDays <= 0 || !Directory.Exists(archiveDir)) return 0;
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(archiveDir, "events_*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var stamp = DateTime.TryParseExact(name.AsSpan(7), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : File.GetLastWriteTime(f);
            if (now - stamp <= TimeSpan.FromDays(keepDays)) continue;
            try { File.Delete(f); n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"アーカイブを削除できません {name}: {ex.Message}"); }
        }
        return n;
    }

    /// <summary>設定に従って archive を掃除する。</summary>
    public static int PruneArchive(Paths paths, Config cfg) => PruneArchive(Path.Combine(paths.Data, "archive"), cfg.ArchiveKeepDays, DateTime.Now);
}
