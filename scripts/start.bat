@echo off
rem 単一 exe で釣り+ダッシュボードを常駐起動する(リポジトリ直下を --root にする)
rem 自動更新で入れ替わる csharp\dist を優先し、無ければ dotnet publish の出力(artifacts\publish)を使う
cd /d "%~dp0.."
set EXE=csharp\dist\pocofarm.exe
if not exist "%EXE%" set EXE=artifacts\publish\pocofarm.exe
if not exist "%EXE%" (
  echo pocofarm.exe was not found in csharp\dist or artifacts\publish
  exit /b 1
)
start "PocoFarm" /min "%EXE%" all --root .
