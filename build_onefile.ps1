Set-Location $PSScriptRoot
pip install -r requirements-build.txt
pyinstaller interview-assistant.spec --noconfirm
Write-Host "Done: dist\InterviewAssistant.exe (one file)"
