@echo off
rem 単一 exe で釣り+ダッシュボードを常駐起動する(リポジトリ直下を --root にする)
cd /d "%~dp0.."
start "PocoFarm" /min artifacts\publish\pocofarm.exe all --root .
