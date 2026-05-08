"""Per-process bridge session token for localhost-only POST/GET protection."""

from __future__ import annotations

import secrets

_bridge_session_secret: str = ""


def generate_bridge_token() -> str:
    """Generate and store a new token for this bridge process."""
    global _bridge_session_secret
    _bridge_session_secret = secrets.token_urlsafe(32)
    return _bridge_session_secret


def get_bridge_token() -> str:
    return _bridge_session_secret


def validate_bridge_token(header_value: str | None) -> bool:
    exp = _bridge_session_secret
    if not exp:
        return False
    got = (header_value or "").strip()
    if len(got) != len(exp):
        return False
    return secrets.compare_digest(got, exp)
