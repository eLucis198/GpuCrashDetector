@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-startup-task.ps1"

if errorlevel 1 (
    echo Failed to install the startup task.
    exit /b %errorlevel%
)

echo Startup task installed successfully.
