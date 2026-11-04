namespace Pocofarm.Fishing;

/// <summary>同じ失敗が続くときは、最初の 1 回とそのあと N 回ごとだけ証拠画像を残す(イベントは毎回書く)。釣れたときは常に残す。</summary>
internal sealed class EvidenceThrottle(int every = 10)
{
    string last = "";
    int count;

    public bool Keep(string outcome)
    {
        if (outcome == last) count++;
        else { last = outcome; count = 1; }
        return outcome == "caught" || count == 1 || count % every == 0;
    }
}
