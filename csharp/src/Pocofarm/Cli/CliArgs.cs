using Pocofarm.Core;

namespace Pocofarm.Cli;

/// <summary>コマンドライン引数の取り出し。</summary>
public static class CliArgs
{
    /// <summary>"--name value" 形式の引数を取り出す。</summary>
    public static string? Get(string[] args, string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    /// <summary>--root(省略時はカレントディレクトリ)を基準にしたパス集合。</summary>
    public static Paths Paths(string[] args) => new(Path.GetFullPath(Get(args, "--root") ?? Directory.GetCurrentDirectory()));
}
