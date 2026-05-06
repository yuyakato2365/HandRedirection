@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Tools\SpatialAnchorCalibration\pc_anchor_control_window.ps1"
