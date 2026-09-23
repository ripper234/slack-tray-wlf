@echo off
setlocal
rem The whole block is parsed before uninstall removes this installed launcher.
(
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
    if errorlevel 1 (
        echo.
        echo Uninstall was not completed. Keep the installed files and retry after resolving the message above.
        pause
        exit /b 1
    )
    echo.
    pause
    exit /b 0
)
