@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-release.ps1"

if errorlevel 1 (
    echo Failed to publish the release package.
    exit /b %errorlevel%
)

echo Release package published successfully.
