# C# 版 pocofarm.exe のダッシュボード(TCP 10000)をLANから開けるようにする。管理者権限が必要(UACの確認が出ます)。
# 使い方: powershell -File scripts\setup_firewall.ps1 -Exe csharp\dist\pocofarm.exe
#   1. exe 宛ての既存のブロック規則(過去の「キャンセル」で作られる)を削除する
#   2. TCP 10000 の許可規則(プライベートプロファイル/同一サブネット限定。-Subnet で変更可)を作る
# 注意: 昇格後のプロセスは N: などのマップドライブを見られないため、パス解決は昇格前に行い引数で渡す。
param([string]$Exe, [string]$Log, [string]$Subnet = 'LocalSubnet')
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $full = (Resolve-Path -LiteralPath $Exe).Path
    $target = (Get-Item -LiteralPath $full).Target
    $progs = @($full, $target) | Where-Object { $_ } | Select-Object -Unique
    $copy = Join-Path $env:TEMP 'pocofarm_setup_firewall_exe.ps1'
    $logFile = Join-Path $env:TEMP 'pocofarm_setup_firewall_exe.log'
    Copy-Item -LiteralPath $PSCommandPath -Destination $copy -Force
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$copy`" -Log `"$logFile`" -Subnet `"$Subnet`" -Exe " + (($progs | ForEach-Object { "`"$_`"" }) -join ',')
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    Write-Host "管理者権限で再実行します。UACを承認してください。結果: $logFile"
    exit
}

Start-Transcript -Path $Log -Force | Out-Null
trap { Write-Host "エラー: $_" -ForegroundColor Red; Read-Host 'Enterで閉じる'; exit 1 }

# 1. ブロック規則の削除(netsh でパス指定して消す)
$blockPaths = Get-NetFirewallRule -Direction Inbound -Action Block | ForEach-Object { ($_ | Get-NetFirewallApplicationFilter).Program } |
    Where-Object { $_ -match 'pocofarm.*\.exe$' } | Select-Object -Unique
foreach ($bp in $blockPaths) {
    Write-Host "ブロック規則を削除: $bp"
    netsh advfirewall firewall delete rule name="pocofarm.exe" dir=in program="$bp" | Write-Host
}

# 2. 許可規則の作り直し
foreach ($p in $Exe) {
    $name = "PocoFarm C# dashboard TCP 10000 ($p)"
    Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Action Allow -Protocol TCP -LocalPort 10000 `
        -Program $p -Profile Private -RemoteAddress $Subnet | Out-Null
    Write-Host "許可規則を作成: $p"
}
Write-Host "完了。http://<このPCのIP>:10000/ をLAN内の別端末から開いてください。"
Read-Host "Enterで閉じる"
