@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\SpatialAnchorCalibration\pc_anchor_quick_control.ps1"
