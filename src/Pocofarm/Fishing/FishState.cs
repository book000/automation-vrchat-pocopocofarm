using System.Text;

namespace Pocofarm.Fishing;

/// <summary>釣りの状態機械の状態。status.json の "state" には <see cref="FishStateExtensions.Wire"/> の snake_case 文字列で出す。</summary>
public enum FishState
{
    WaitWindow,
    Cooldown,
    CastWait,
    WaitBite,
    ResultWait,
}

public static class FishStateExtensions
{
    static readonly string[] Names = Enum.GetNames<FishState>().Select(ToSnake).ToArray();

    /// <summary>status.json・events.jsonl・ダッシュボードが使う状態名(wait_window, cooldown, cast_wait, wait_bite, result_wait)。</summary>
    public static string Wire(this FishState s) => Names[(int)s];

    static string ToSnake(string pascal)
    {
        var sb = new StringBuilder();
        foreach (var c in pascal)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
