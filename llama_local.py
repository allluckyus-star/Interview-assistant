"""Local Llama/Ollama client for transcript cleaning."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any, Dict

from prompt_templates import build_local_summary_prompt


DEFAULT_OLLAMA_URL = "http://127.0.0.1:11434/api/generate"


def _post_json(url: str, payload: Dict[str, Any], timeout_s: int) -> Dict[str, Any]:
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=timeout_s) as response:
        raw = response.read().decode("utf-8")
    return json.loads(raw)


def summarize_for_interview_question(
    raw_chunk: str,
    model: str = "llama3:latest",
    url: str = DEFAULT_OLLAMA_URL,
    timeout_s: int = 30,
) -> Dict[str, Any]:
    """Return cleaned plain-text intent while preserving all interview questions."""
    prompt = build_local_summary_prompt(raw_chunk)
    print("\n[LLAMA INPUT]\n" + raw_chunk + "\n")
    payload = {
        "model": model,
        "prompt": prompt,
        "stream": False,
        "options": {"temperature": 0.0},
    }
    try:
        result = _post_json(url, payload, timeout_s)
        response_text = result.get("response", "").strip()
        print("\n[LLAMA OUTPUT]\n" + response_text + "\n")
        cleaned = response_text.strip() or raw_chunk.strip()
        return {"cleaned_interviewer_intent": cleaned}
    except urllib.error.URLError as e:
        # Fallback: preserve raw chunk to avoid question loss.
        print(f"\n[LLAMA OUTPUT]\n[FALLBACK] Connection error: {e}\nUsing raw chunk\n")
        return {
            "cleaned_interviewer_intent": raw_chunk.strip(),
            "fallback_used": True,
        }
    except TimeoutError as e:
        print(f"\n[LLAMA OUTPUT]\n[FALLBACK] Timeout error: {e}\nUsing raw chunk\n")
        return {
            "cleaned_interviewer_intent": raw_chunk.strip(),
            "fallback_used": True,
        }
    except ValueError as e:
        print(f"\n[LLAMA OUTPUT]\n[FALLBACK] JSON parse error: {e}\nUsing raw chunk\n")
        return {
            "cleaned_interviewer_intent": raw_chunk.strip(),
            "fallback_used": True,
        }
    except Exception as e:
        print(f"\n[LLAMA OUTPUT]\n[FALLBACK] Unexpected error: {e}\nUsing raw chunk\n")
        return {
            "cleaned_interviewer_intent": raw_chunk.strip(),
            "fallback_used": True,
        }
