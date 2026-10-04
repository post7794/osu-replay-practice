@echo off
setlocal
cd /d "%~dp0"
start "" /d "%~dp0" "%~dp0osu!.exe" %*
