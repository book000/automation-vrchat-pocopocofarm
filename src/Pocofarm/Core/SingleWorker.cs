using System.Collections.Concurrent;

namespace Pocofarm.Core;

/// <summary>専用スレッドで仕事を 1 つずつ実行する。OCR のようにスレッドごとに状態を持つ処理を逃がすのに使う。</summary>
public sealed class SingleWorker : IDisposable
{
    readonly BlockingCollection<Action> jobs = new();

    public SingleWorker(string name)
    {
        new Thread(() => { foreach (var j in jobs.GetConsumingEnumerable()) j(); }) { IsBackground = true, Name = name }.Start();
    }

    /// <summary>仕事を投入し、結果の Task を返す。例外は Task に載る。</summary>
    public Task<T> Submit<T>(Func<T> job)
    {
        var tcs = new TaskCompletionSource<T>();
        jobs.Add(() => { try { tcs.SetResult(job()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    public void Dispose() => jobs.CompleteAdding();
}
