using System.Globalization;

namespace Pocofarm.Core;

/// <summary>テキストログ(5MB×5 世代のローテーション付き)。data/fishing.log に追記する。crash.log も Append で同じ規則で回す。</summary>
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
            Append(path, line + "\n");
        }
    }

    /// <summary>ファイルへ追記する。5MB を超えていたら 5 世代で回す。失敗しても呼び出し側を止めない。crash.log など fishing.log 以外にも使う。</summary>
    public static void Append(string file, string text)
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(file) && new FileInfo(file).Length > 5_000_000) Rotate(file);
                // ダッシュボードなど他プロセスが同じファイルを開いていても追記できるよう共有を許す
                using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(System.Text.Encoding.UTF8.GetBytes(text));
            }
            catch (IOException) { /* ログ書き込み失敗で釣りを止めない */ }
        }
    }

    static void Rotate(string file)
    {
        for (int i = 4; i >= 1; i--)
            if (File.Exists($"{file}.{i}")) File.Move($"{file}.{i}", $"{file}.{i + 1}", true);
        File.Move(file, $"{file}.1", true);
    }
}
