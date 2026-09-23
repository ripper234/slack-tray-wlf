@echo off
setlocal
rem Run in a new window so this batch file is closed before installation replaces it.
start "" "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" -PauseAtEnd
