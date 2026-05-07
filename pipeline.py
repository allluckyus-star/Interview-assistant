"""Processing pipeline: chunk -> direct GPT prompt -> bridge store."""

from __future__ import annotations

from typing import Callable, Optional

from bridge_server import PromptStore


def build_chunk_prompts(
    raw_chunk: str,
    prompt_store: Optional[PromptStore] = None,
    template_override: Optional[str] = None,
) -> tuple[str, str]:
    """Build (cleaned_intent, final_prompt) from the extension-supplied chunk template only.

    Resolution order for the template body:
      1. ``template_override`` (used to pin the prompt at interview-start — pass an empty string
         to explicitly mean "no template, send the cleaned caption verbatim").
      2. ``prompt_store.get_template("chunk_interview")`` (live extension value).
    """
    cleaned_intent = (raw_chunk.strip() or raw_chunk).strip()
    if template_override is not None:
        template = template_override.strip()
    else:
        template = ""
        if prompt_store is not None:
            try:
                template = (prompt_store.get_template("chunk_interview") or "").strip()
            except Exception:
                template = ""
    if template:
        final_prompt = template.replace("{cleaned_interviewer_intent}", cleaned_intent)
    else:
        final_prompt = cleaned_intent
    return cleaned_intent, final_prompt


def process_caption_chunk(
    raw_chunk: str,
    prompt_store: PromptStore,
    log_fn: Callable[[str, str], None],
    template_override: Optional[str] = None,
) -> dict:
    """Bypass local Llama and publish final GPT-site prompt directly."""
    cleaned_intent, final_prompt = build_chunk_prompts(
        raw_chunk, prompt_store, template_override=template_override
    )
    metadata = prompt_store.set_prompt(final_prompt)
    log_fn("Prompt queued", str(metadata.get("request_id", "")))
    return {
        "cleaned_interviewer_intent": cleaned_intent.strip(),
        "request_id": metadata.get("request_id", ""),
    }
