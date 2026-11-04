using System.Globalization;

namespace Pocofarm.Core;

/// <summary>テキストログ(5MB×5 世代のローテーション付き)。data/fishing.log に追記する。</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? path;
    static bool console = true;

    /// <summary>出力先を設定する。</summary>
    public static void Setup(string logPath, bool toConsole = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        path = logPath;
        console = toConsole;
    }

    public static void Info(string msg) => Write("INFO", msg);

    public static void Warn(string msg) => Write("WARNING", msg);

    public static void Error(string msg) => Write("ERROR", msg);

    public static void Exception(string msg, Exception ex) => Write("ERROR", $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level} {msg}";
        lock (Gate)
        {
            if (console) Console.WriteLine(line);
            if (path is null) return;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 5_000_000) Rotate();
                // ダッシュボードなど他プロセスが同じファイルを開いていても追記できるよう共有を許す
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(System.Text.Encoding.UTF8.GetBytes(line + "\n"));
            }
            catch (IOException) { /* ログ書き込み失敗で釣りを止めない */ }
        }
    }

    static void Rotate()
    {
        for (int i = 4; i >= 1; i--)
            if (File.Exists($"{path}.{i}")) File.Move($"{path}.{i}", $"{path}.{i + 1}", true);
        File.Move(path!, $"{path}.1", true);
    }
}
