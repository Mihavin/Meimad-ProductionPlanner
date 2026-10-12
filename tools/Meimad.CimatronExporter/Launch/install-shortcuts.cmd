@echo off
rem Double-click: creates the "Cimatron to Meimad" desktop and Start-menu shortcuts.
rem install-shortcuts.cmd -Remove deletes them.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-shortcuts.ps1" %*
pause
