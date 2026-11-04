using Pocofarm.Core;
using Pocofarm.Ocr;
using Pocofarm.Vision;

namespace Pocofarm.Fishing;

/// <summary>引いた 1 回分の文脈。</summary>
public sealed class Attempt(int id, double tDip, double tClick, (int X, int Y)? posBefore, int pecks)
{
    public int Id { get; } = id;
    public double TDip { get; } = tDip;
    public double TClick { get; } = tClick;
    public (int X, int Y)? PosBefore { get; } = posBefore;
    public int Pecks { get; } = pecks;
    public List<double[]> Trace { get; set; } = [];
    public Toast? Best { get; set; }
    public double? FirstSeen { get; set; }
    public Frame? FrameClick { get; set; }
    public bool Probe { get; set; }
    public List<string> Info { get; } = [];           // 同時に出たレベルアップ/実績通知
    public double InfoUntil { get; set; }
    public string PartialNote { get; set; } = "";
    public string Engine { get; set; } = "";          // 結果を確定させた OCR 経路
    public double TCast { get; set; }                  // 直近の投げクリックの時刻
    public double TArmed { get; set; }                 // 着水静止から最初の沈みまでの待ち時間を測る起点
    public Task<RecoverResult>? Recovery { get; set; }  // OCR スレッドでの再読取り(進行中)
    public double RecoveryStarted { get; set; }
    public bool RecoveryTried { get; set; }
    public double BaseW { get; set; }                 // 引いたときのウキ幅(投げ位置の遠近の目安)
}
