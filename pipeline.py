"""Processing pipeline: chunk -> direct GPT prompt -> bridge store."""

from __future__ import annotations

from typing import Callable

from bridge_server import PromptStore
from prompt_templates import build_final_answer_prompt


def build_chunk_prompts(
    raw_chunk: str,
    resume_text: str,
    job_description_text: str,
    additional_context_text: str,
) -> tuple[str, str]:
    cleaned_intent = raw_chunk.strip() or raw_chunk
    final_prompt = build_final_answer_prompt(
        cleaned_interviewer_intent=cleaned_intent.strip(),
        resume_text=resume_text.strip(),
        job_description_text=job_description_text.strip(),
        additional_context_text=additional_context_text.strip(),
    )
    return cleaned_intent.strip(), final_prompt


def process_caption_chunk(
    raw_chunk: str,
    prompt_store: PromptStore,
    resume_text: str,
    job_description_text: str,
    additional_context_text: str,
    log_fn: Callable[[str, str], None],
) -> dict:
    """Bypass local Llama and publish final GPT-site prompt directly."""
    cleaned_intent, final_prompt = build_chunk_prompts(
        raw_chunk, resume_text, job_description_text, additional_context_text
    )
    metadata = prompt_store.set_prompt(final_prompt)
    log_fn("Prompt queued", str(metadata.get("request_id", "")))
    return {
        "cleaned_interviewer_intent": cleaned_intent.strip(),
        "request_id": metadata.get("request_id", ""),
    }
