@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -Command "Start-Process -FilePath (Join-Path (Get-Location) 'OnlyFansControl.exe') -Verb RunAs -WindowStyle Hidden"
