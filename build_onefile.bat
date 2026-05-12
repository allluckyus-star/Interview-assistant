@echo off
setlocal
cd /d "%~dp0"

echo Building InterviewAssistant.exe ...
python -m pip install -r requirements-build.txt
if errorlevel 1 goto :err

pyinstaller interview-assistant.spec --noconfirm
if errorlevel 1 goto :err

echo.
echo Done: dist\InterviewAssistant.exe (one file)
pause
exit /b 0

:err
echo.
echo Build failed.
pause
exit /b 1
