namespace Pocofarm.Core;

/// <summary>実行時のパス。ルート(config.json と data/ のある場所)を引数で指定する。</summary>
public sealed class Paths(string root)
{
    public string Root { get; } = root;
    public string Data => Path.Combine(Root, "data");
    public string Events => Path.Combine(Data, "events.jsonl");
    public string Log => Path.Combine(Data, "fishing.log");
    public string Status => Path.Combine(Data, "status.json");
    public string Live => Path.Combine(Data, "live.jpg");

    /// <summary>ファーム撮影・画角調整の間、Spout 映像を 1 枚ずつ書き出すファイル(ダッシュボードのリアルタイム表示用)。</summary>
    public string FarmLive => Path.Combine(Data, "farm_live.jpg");
    public string Control => Path.Combine(Data, "control.json");
    public string Reset => Path.Combine(Data, "reset.json");
    public string Calibration => Path.Combine(Data, "calibration.json");
    public string Tuning => Path.Combine(Data, "tuning.json");
    public string CameraPose => Path.Combine(Data, "camera_pose.json");
    public string Farm => Path.Combine(Data, "farm");
    public string Evidence => Path.Combine(Data, "evidence");
    public string ConfigFile => Path.Combine(Root, "config.json");
}
