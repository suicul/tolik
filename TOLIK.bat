@echo off
REM TOLIK double-click launcher. Runs scripts\start-tolik.ps1 with the given flags.
REM Usage: TOLIK.bat [-NoAgent] [-NoBrowser] [-NoXtts]
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-tolik.ps1" %*
