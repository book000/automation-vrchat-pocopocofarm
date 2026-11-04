using Pocofarm.Core;
using Pocofarm.Platform;

namespace Pocofarm.Fishing;

public sealed partial class Fisher
{
    bool resumeCheckPending;
    double lastResumeCheckLog;

    /// <summary>ダッシュボードからの手動操作(再投げ・今すぐ引く・視点を少し動かす)を処理する。一時停止中・ドライラン中の要求は受け口で弾いてある。</summary>
    void HandleManual(double now, Frame f)
    {
        while (control.Commands.TryDequeue(out var c))
        {
            events.Write("command", ("cmd", c.Name), ("arg", c.Arg), ("state", state.Wire()));
            Log.Info($"手動操作: {c.Name} {c.Arg}");
            switch (c.Name)
            {
                case "recast":
                    ClearAttention("recast");
                    if (state is FishState.WaitBite or FishState.CastWait && attempt is null) NoBite(now, f);      // 確認クリックで水中のウキを引き上げ、そのまま投げ直しへ進む
                    else if (state == FishState.Cooldown) tState = Now - cfg.RecastDelay;
                    break;
                case "reel_now":
                    if (state == FishState.WaitBite) DoReel(now, f, "手動で引く");
                    else RejectManual(c, "not_waiting_for_bite");
                    break;
                case "nudge":
                    Nudge(c.Arg ?? "", c.Amount);
                    break;
            }
        }
    }

    void RejectManual(ControlCommand c, string reason)
    {
        Log.Warn($"手動操作を実行できません: {c.Name} ({reason})");
        events.Write("command_rejected", ("cmd", c.Name), ("reason", reason));
    }

    /// <summary>視点をマウスで少しだけ動かす。VRChat を一時的に前面にし、終わったら元のウィンドウとカーソル位置へ戻す。</summary>
    void Nudge(string dir, int px)
    {
        px = Math.Clamp(px, 1, ControlChannel.MaxNudgePx);
        var (dx, dy) = dir switch { "left" => (-px, 0), "right" => (px, 0), "up" => (0, -px), "down" => (0, px), _ => (0, 0) };
        if ((dx, dy) == (0, 0) || !WinApi.IsAlive(hwnd)) return;
        var prevFg = WinApi.ForegroundWindow();
        var cursor = WinApi.CursorPos();
        if (!WinApi.EnsureForeground(hwnd)) { Log.Warn("VRChat を前面にできないため視点移動を見送ります"); return; }
        try { WinApi.MouseMove(dx, dy); }
        finally
        {
            WinApi.MoveCursor(cursor.X, cursor.Y);
            if (prevFg != 0 && prevFg != hwnd) WinApi.EnsureForeground(prevFg);
        }
        lastTick = Now;
        OnViewChange();      // 視点が変わったので、静止判定をやり直す
    }

    /// <summary>
    /// 再開後の最初のクリックの前に確認する: VRChat のウィンドウがあり最小化されていない、キャプチャが新しい(設定で前面も必須にできる)。
    /// ウキが見えているかは結果と一緒に記録する。釣り竿を持っているかを画面から判定する手段はないため、確認しない。
    /// 満たすまでクリックは保留される。
    /// </summary>
    bool ResumeCheckPassed()
    {
        if (!resumeCheckPending) return true;
        if (!cfg.ResumeCheck || dryRun) { resumeCheckPending = false; return true; }
        string? problem = null;
        if (hwnd == 0 || !WinApi.IsAlive(hwnd)) problem = "VRChat ウィンドウなし";
        else if (WinApi.IsMinimized(hwnd)) problem = "VRChat が最小化されている";
        else if (cfg.ResumeRequireForeground && WinApi.ForegroundWindow() != hwnd) problem = "VRChat が前面にない";
        else if (cap is null || Now - cap.LatestAt > cfg.ResumeFrameMaxAge) problem = "キャプチャが更新されていない";
        if (problem is null)
        {
            resumeCheckPending = false;
            events.Write("resume_check", ("ok", true), ("bobber_visible", bobber is not null));
            Log.Info($"再開前の確認 OK(ウキ{(bobber is null ? "なし" : "あり")})");
            return true;
        }
        status.Set("message", $"再開前の確認待ち: {problem}");
        if (Now - lastResumeCheckLog > 5)
        {
            lastResumeCheckLog = Now;
            events.Write("resume_check", ("ok", false), ("problem", problem));
            Log.Warn($"再開前の確認に失敗: {problem}。クリックを保留します");
        }
        return false;
    }
}
