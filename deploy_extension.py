"""One-time deploy of bundled Chrome extension to C:\\interview (Windows, frozen exe or dev)."""

from __future__ import annotations

import ctypes
import os
import shutil
import sys
from pathlib import Path

# Load unpacked extension from here in Chrome (developer mode).
EXTENSION_INSTALL_DIR = Path(r"C:\interview")


def _runtime_root() -> Path:
    if getattr(sys, "frozen", False):
        meipass = getattr(sys, "_MEIPASS", None)
        if not meipass:
            raise RuntimeError("Frozen build missing sys._MEIPASS")
        return Path(meipass)
    return Path(__file__).resolve().parent


def bundled_extension_source() -> Path:
    """Directory whose contents should mirror the Chrome extension root (manifest.json, etc.)."""
    ext = _runtime_root() / "extension"
    if ext.is_dir():
        return ext
    raise RuntimeError(f'Bundled "extension" folder not found under {_runtime_root()}')


def _win_message(title: str, text: str, *, warning: bool = True) -> None:
    if os.name != "nt":
        print(text, file=sys.stderr)
        return
    MB_ICONWARNING = 0x30
    MB_ICONERROR = 0x10
    flags = MB_ICONWARNING if warning else MB_ICONERROR
    ctypes.windll.user32.MessageBoxW(None, text, title, flags)


def ensure_extension_deployed() -> None:
    """If C:\\interview is missing, copy the bundled extension there; if it exists, do nothing."""
    if os.name != "nt":
        return
    if EXTENSION_INSTALL_DIR.exists():
        return
    try:
        src = bundled_extension_source()
    except RuntimeError as exc:
        _win_message("Interview Assistant", str(exc))
        sys.exit(1)
    try:
        shutil.copytree(src, EXTENSION_INSTALL_DIR)
    except OSError as exc:
        _win_message(
            "Interview Assistant",
            "Could not copy the extension to C:\\interview.\n\n"
            f"{exc}\n\nTry running this program as Administrator once, or create C:\\interview manually.",
        )
        sys.exit(1)
