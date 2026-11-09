@echo off
setlocal
cd /d "%~dp0"
where py >nul 2>nul
if errorlevel 1 goto python_command
py -3 build.py
exit /b %errorlevel%
:python_command
python build.py
exit /b %errorlevel%
