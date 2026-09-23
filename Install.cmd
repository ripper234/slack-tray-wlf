@echo off
setlocal
(
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
    if errorlevel 1 (
        echo.
        echo Installation failed. Read the message above.
        pause
        exit /b 1
    )
    echo.
    pause
    exit /b 0
)
