# -*- mode: python ; coding: utf-8 -*-
# Build: pyinstaller interview-assistant.spec
# Output: dist/InterviewAssistant.exe

a = Analysis(
    ["live.py"],
    pathex=[],
    binaries=[],
    datas=[("extension", "extension")],
    hiddenimports=["websockets"],
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
