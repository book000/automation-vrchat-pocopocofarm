@echo off
rem 単一 exe で釣り+ダッシュボードを常駐起動する
cd /d "%~dp0"
start "PocoFarm" /min csharp\dist\pocofarm.exe all --root .
