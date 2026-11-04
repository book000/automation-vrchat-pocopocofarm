using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pocofarm.Core;

/// <summary>不具合調査用の診断 zip。ログ・状態・伏せ字にした設定・直近の失敗証拠・環境情報をまとめる。</summary>
public static class DiagBundle
{
    const int LogLines = 500;
    const int EvidenceCount = 10;
    const long EvidenceBudget = 20L * 1024 * 1024;

    /// <summary>この名前を含むキー(大文字小文字を区別しない)は設定から取り除く。update_source は名前に関わらず取り除く。</summary>
    static readonly string[] SecretWords = ["url", "token", "secret", "password"];

    /// <summary>JSON から秘匿すべきキーを(入れ子も含めて)取り除く。</summary>
    public static void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (key == "update_source" || SecretWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase))) o.Remove(key);
                    else Redact(o[key]);
                }
                break;
            case JsonArray a:
                foreach (var x in a) Redact(x);
                break;
        }
    }

    /// <summary>JSON 文字列を伏せ字にして整形して返す。JSON として読めなければ null。</summary>
    public static string? RedactedJson(string text)
    {
        try
        {
            var n = JsonNode.Parse(text);
            Redact(n);
            return n?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException) { return null; }
    }

    /// <summary>診断 zip を output へ書く。個々のファイルが読めなくても全体は作る(欠けた旨を README に書く)。</summary>
    public static void Build(Paths paths, Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var notes = new List<string>();

        void Text(string name, string? content)
        {
            if (content is null) return;
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        string? ReadAll(string path)
        {
            try { return File.Exists(path) ? Encoding.UTF8.GetString(ReadShared(path)) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"{Path.GetFileName(path)}: {ex.Message}"); return null; }
        }
        string? Tail(string path)
        {
            var t = ReadAll(path);
            return t is null ? null : string.Join('\n', t.Split('\n').TakeLast(LogLines + 1)).TrimEnd() + "\n";
        }
        string? Json(string path, string label)
        {
            var t = ReadAll(path);
            if (t is null) return null;
            var r = RedactedJson(t);
            if (r is null) notes.Add($"{label} は JSON として読めないため含めていません");
            return r;
        }

        Text("fishing.log.tail.txt", Tail(paths.Log));
        Text("crash.log.tail.txt", Tail(paths.CrashLog));
        Text("status.json", Json(paths.Status, "status.json"));
        Text("config.redacted.json", Json(paths.ConfigFile, "config.json"));
        Text("selfcheck.json", ReadAll(paths.SelfCheck));
        Text("system.txt", SystemInfo());
        AddEvidence(zip, paths, notes);
        Text("README.txt", "Pocofarm diagnostics bundle\n"
            + $"created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"
            + "config.redacted.json has update_source and keys containing url/token/secret/password removed.\n"
            + (notes.Count > 0 ? "notes:\n" + string.Join('\n', notes.Select(n => "  - " + n)) + "\n" : ""));
    }

    static void AddEvidence(ZipArchive zip, Paths paths, List<string> notes)
    {
        if (!Directory.Exists(paths.Evidence)) return;
        long used = 0;
        foreach (var jpg in new DirectoryInfo(paths.Evidence).EnumerateFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc).Take(EvidenceCount))
        {
            var json = new FileInfo(Path.ChangeExtension(jpg.FullName, ".json"));
            long size = jpg.Length + (json.Exists ? json.Length : 0);
            if (used + size > EvidenceBudget) { notes.Add($"容量上限のため証拠 {jpg.Name} 以降は含めていません"); break; }
            try
            {
                Copy(zip, "evidence/" + jpg.Name, jpg.FullName);
                if (json.Exists) Copy(zip, "evidence/" + json.Name, json.FullName);
                used += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"{jpg.Name}: {ex.Message}"); }
        }
    }

    static void Copy(ZipArchive zip, string name, string path)
    {
        var bytes = ReadShared(path);
        var e = zip.CreateEntry(name, name.EndsWith(".jpg", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(bytes);
    }

    static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>exe のバージョン・OS・.NET・CPU・GPU(レジストリの表示アダプタ名)。</summary>
    static string SystemInfo()
    {
        var sb = new StringBuilder();
        var asm = Assembly.GetExecutingAssembly();
        sb.AppendLine($"version: {asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? asm.GetName().Version?.ToString()}");
        sb.AppendLine($"exe: {Environment.ProcessPath}");
        sb.AppendLine($"os: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"dotnet: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"cpu_count: {Environment.ProcessorCount}");
        sb.AppendLine($"gpu: {string.Join(" | ", Gpus())}");
        return sb.ToString();
    }

    static List<string> Gpus()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in cls?.GetSubKeyNames() ?? [])
            {
                using var k = cls!.OpenSubKey(name);
                if (k?.GetValue("DriverDesc") is string d) list.Add(d);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }
}
