"""IA_DEBUG-controlled printing and safe text previews for logs."""

from __future__ import annotations

import os

DEBUG = os.getenv("IA_DEBUG", "").strip() == "1"


def debug_log(*args: object, **kwargs: object) -> None:
    if DEBUG:
        kwargs.setdefault("flush", True)
        print(*args, **kwargs)


def safe_preview(text: str, limit: int = 40) -> str:
    text = str(text or "").replace("\r", "\\r").replace("\n", "\\n")
    if len(text) > limit:
        return text[:limit] + "..."
    return text
