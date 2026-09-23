@echo off
setlocal
(
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0status.ps1"
    if errorlevel 1 (
        echo.
        pause
        exit /b 1
    )
    echo.
    pause
    exit /b 0
)
