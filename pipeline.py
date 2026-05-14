"""Processing pipeline: chunk -> direct GPT prompt -> bridge store."""

from __future__ import annotations

from typing import Callable, Optional

from bridge_server import PromptStore


def build_chunk_prompts(
    raw_chunk: str,
    prompt_store: Optional[PromptStore] = None,
    template_override: Optional[str] = None,
) -> tuple[str, str]:
    """Build (cleaned_intent, final_prompt) for a caption or manual chunk.

    If ``template_override`` is not None, its stripped text is the template (use ``""`` to send
    ``cleaned_intent`` verbatim). If ``template_override`` is None, no template is applied and
    ``final_prompt`` equals the cleaned chunk text.
    """
    cleaned_intent = (raw_chunk.strip() or raw_chunk).strip()
    if template_override is not None:
        template = template_override.strip()
    else:
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
