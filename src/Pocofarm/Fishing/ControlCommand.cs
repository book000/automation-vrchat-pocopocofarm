namespace Pocofarm.Fishing;

/// <summary>ダッシュボードからの手動操作 1 件。Arg は nudge の方向、Amount は nudge の量[px](上限あり)。</summary>
public sealed record ControlCommand(string Name, string? Arg, int Amount);
