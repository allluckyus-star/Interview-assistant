"""Prompt builders for local summarization and GPT website answer generation."""

from __future__ import annotations


def build_local_summary_prompt(raw_text: str) -> str:
    """Build plain-text summarization prompt that preserves technical questions."""
    return f"""You are an interview transcript cleaner. Your job is to clean the transcript EXACTLY as spoken, without adding anything.

CRITICAL RULES:
1) If the interviewer asked a question, keep it verbatim in meaning (you may shorten surrounding words, but NOT the question itself).
2) If there are NO questions in the transcript, do NOT invent or create any questions. Just clean and return the statements as-is.
3) Remove filler words (um, uh, like), repeated confirmations, and noise.
4) Keep key constraints: tech stack, years, domain, role level, location, work mode, rate/salary, timeline.
5) If the transcript is ONLY meaningless filler (hello, weather, "can you hear me"), you may return empty, but if there's ANY substantive content, keep it.

Length:
- Use fewer than 5 sentences (at most 4 complete sentences).
- If you need more space, merge ideas; do not drop any question or important statement.

Output rules:
- Return plain text only. No JSON, no headings, no labels, no bullet points.
- Do not include any preamble or meta text (no "Here is", "Below is", "Cleaned transcript", "Summary", etc.).
- Start directly with the cleaned transcript content.

Transcript:
\"\"\"{raw_text}\"\"\"
"""


def build_initial_session_prompt(
    resume_text: str,
    job_description_text: str,
    additional_context_text: str,
) -> str:
    """First message to ChatGPT when the interview session starts (PageDown)."""
    return f"""You are helping a candidate during a live technical interview.

You will receive short excerpts of what the interviewer said (as captions). When sent, reply with only the exact words the candidate should say next — concise, first person, no meta commentary.

Before that, here is static context:

Candidate resume (may be summarized):
{resume_text.strip()}

Job description (may be summarized):
{job_description_text.strip()}

Additional instructions:
{additional_context_text.strip()}

Reply with one short sentence confirming you are ready, then stop."""


def build_final_answer_prompt(
    cleaned_interviewer_intent: str,
    resume_text: str,
    job_description_text: str,
    additional_context_text: str,
) -> str:
    """Build a strict final prompt for GPT website submission."""
    return f"""You are generating what the candidate should say in an interview.

Hard rules (non-negotiable):
- Output only the final answer text the candidate should speak.
- No explanation, no analysis, no headings, no labels, no bullet list unless interviewer explicitly asked for a list.
- Use first person.
- Keep concise and natural, interview-ready tone.
- If multiple questions are present, answer them in asked order in compact paragraph form.
- If there is no clear question, output one short clarification question only.

Interviewer intent (cleaned):
{cleaned_interviewer_intent}

Candidate resume context:
{resume_text}

Job description context:
{job_description_text}

Additional context:
{additional_context_text}
"""
