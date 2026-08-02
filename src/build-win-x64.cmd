@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "NO_PAUSE_ARG="
set "NO_START_ARG="
for %%A in (%*) do (
    if /I "%%~A"=="--no-pause" set "NO_PAUSE_ARG=-NoPause"
    if /I "%%~A"=="--no-start" set "NO_START_ARG=-NoStart"
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-win-x64.ps1" !NO_PAUSE_ARG! !NO_START_ARG!
exit /b %ERRORLEVEL%
