using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace Pocofarm.Platform;

/// <summary>ダッシュボード(TCP)をLANから開けるファイアウォール規則の確認と作成(管理者権限が必要)。</summary>
public static class Firewall
{
    static string RuleName(string exe, int port) => $"PocoFarm C# dashboard TCP {port} ({exe})";

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static (int Code, string Output) Netsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = CodePagesEncodingProvider.Instance.GetEncoding(932) ?? Encoding.UTF8,
        })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }

    /// <summary>この exe とポートを許可する受信規則が既にあるか。規則名の完全一致検索は日本語環境で当たらないことがあるため、規則の一覧から探す。</summary>
    public static bool HasRule(string exe, int port)
    {
        try
        {
            // 言語に依存しないよう COM(INetFwPolicy2)で規則を列挙する。Action: 1=許可, Direction: 1=受信。プログラム指定なしの許可規則も有効とみなす
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            foreach (dynamic r in policy.Rules)
            {
                if (r.Enabled && r.Direction == 1 && r.Action == 1 && ((string?)r.ApplicationName is not { Length: > 0 } app || string.Equals(app, exe, StringComparison.OrdinalIgnoreCase))
                    && ((string?)r.LocalPorts is not { Length: > 0 } ports || ports == "*" || ports.Split(',').Contains(port.ToString()))) return true;
            }
            return false;
        }
        catch (Exception) { return false; }  // 起動時の案内用なので、失敗しても釣りを止めない
    }

    /// <summary>規則を作る。管理者でなければ UAC で自分自身を昇格して再実行する(昇格後はマップドライブが見えないため、パスは昇格前に渡す)。</summary>
    public static int Setup(string exe, int port, bool elevated)
    {
        if (!IsAdmin())
        {
            if (elevated) { Console.Error.WriteLine("Administrator rights are required."); return 1; }
            var log = Path.Combine(Path.GetTempPath(), "pocofarm_setup.log");
            File.Delete(log);
            Console.WriteLine("管理者権限で再実行します。UAC を承認してください。");
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, $"setup --elevated --port {port} --exe \"{exe}\" --log \"{log}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
                p.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("UAC がキャンセルされました。"); return 1; }
            Console.WriteLine(File.Exists(log) ? File.ReadAllText(log) : "結果を確認できませんでした。");
            return HasRule(exe, port) ? 0 : 1;
        }
        var sb = new StringBuilder();
        // 過去に「キャンセル」で自動作成されたブロック規則(名前は exe 名)は許可規則より優先されるので消す
        sb.AppendLine(Netsh($"advfirewall firewall delete rule name=\"pocofarm.exe\" dir=in program=\"{exe}\"").Output.Trim());
        Netsh($"advfirewall firewall delete rule name=\"{RuleName(exe, port)}\"");
        var r = Netsh($"advfirewall firewall add rule name=\"{RuleName(exe, port)}\" dir=in action=allow protocol=TCP localport={port} program=\"{exe}\" profile=private remoteip=localsubnet");
        sb.AppendLine(r.Code == 0 ? $"許可規則を作成しました: {exe} (TCP {port}, プライベートネットワークのローカルサブネットのみ)" : "失敗: " + r.Output.Trim());
        Console.WriteLine(sb);
        return r.Code;
    }
}
