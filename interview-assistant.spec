# -*- mode: python ; coding: utf-8 -*-
# Build: pyinstaller interview-assistant.spec
# Output: single file dist/InterviewAssistant.exe (onefile).
# Main window always uses exclude-from-capture on Windows (SetWindowDisplayAffinity in live.py).

a = Analysis(
    ["live.py"],
    pathex=[],
    binaries=[],
    datas=[("extension", "extension")],
    hiddenimports=["websockets", "app_prompt_files"],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name="InterviewAssistant",
    onefile=True,
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_tracker=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
