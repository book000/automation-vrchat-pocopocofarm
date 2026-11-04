using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pocofarm.Core;

/// <summary>ファイル書き込みの補助。Windows では読み手がいると置換に失敗するため再試行する。</summary>
public static class AtomicFile
{
    /// <summary>一時ファイル経由で置き換える。読み取り競合時は数回再試行し、失敗しても例外にしない。</summary>
    public static bool Write(string path, byte[] data)
    {
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try { File.WriteAllBytes(tmp, data); }
        catch (IOException ex) { Log.Warn($"書き込み失敗 {Path.GetFileName(path)}: {ex.Message}"); return false; }
        for (int i = 0; i < 8; i++)
        {
            try { File.Move(tmp, path, true); return true; }
            catch (UnauthorizedAccessException) { Thread.Sleep(20); }
            catch (IOException) { Thread.Sleep(20); }
        }
        try { File.Delete(tmp); } catch (IOException) { }
        // 置換できない状態が続いても、読み手は共有で開くので直接書き込めることが多い。心拍が止まって「停止」と誤判定されるよりよい
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            fs.Write(data);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
