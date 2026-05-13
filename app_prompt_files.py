"""Root-directory .txt files for the four prep/interview prompt templates.

Templates are owned by the desktop app (not the browser extension). Missing files
are created with built-in defaults on first ensure; edits persist to disk.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Any, Dict


def app_root_directory() -> Path:
    """Project root when running from source; folder containing the .exe when frozen (onefile)."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent

_TEMPLATE_FILES: Dict[str, str] = {
    "resume_summary": "prompt_resume_summary.txt",
    "jd_summary": "prompt_jd_summary.txt",
    "initial_interview": "prompt_initial_interview.txt",
    "chunk_interview": "prompt_chunk_interview.txt",
}

_DEFAULT_RESUME_SUMMARY = """You are an expert technical interviewer assistant.

Task:
Summarize the candidate's resume into a concise, structured briefing for real-time interview support.

Rules:

* Output ONLY the final summary (no explanation)
* Keep it concise but information-dense
* Focus on signal, not fluff
* No generic phrases

---

Structure:

1. CORE PROFILE (2–3 lines)

* Years of experience
* Primary role (AI / backend / full-stack / etc.)
* Key domains

2. KEY SKILLS

* List top technical strengths
* Prioritize production-level skills

3. SYSTEM EXPERIENCE

* What kinds of systems they built (e.g., ML pipelines, APIs, distributed systems)

4. STRENGTH SIGNALS

* What makes them strong (e.g., scalability, ownership, optimization)

5. WEAK / MISSING AREAS (IMPORTANT)

* Gaps relative to modern expectations

---

Tone:

* Direct
* Technical
* Interview-ready

---

INPUT RESUME:
\"\"\"
{resume_text}
\"\"\"
"""

_DEFAULT_JD_SUMMARY = """You are an expert hiring manager.

Task:
Analyze the job description and extract what the interviewer ACTUALLY cares about.

Rules:

* Output ONLY the structured summary
* Be precise and realistic
* Avoid repeating the JD verbatim

---

Structure:

1. ROLE CORE

* What this role really is (not title, actual function)

2. MUST-HAVE SKILLS (CRITICAL)

* Non-negotiable requirements

3. IMPORTANT SKILLS

* Strongly preferred but flexible

4. NICE-TO-HAVE

* Bonus skills

5. SYSTEM EXPECTATIONS

* What systems candidate should be able to build

6. INTERVIEW FOCUS

* What interviewer will likely test (VERY IMPORTANT)

7. RED FLAGS

* What will cause rejection

---

Tone:

* Hiring manager perspective
* Practical, not theoretical

---

INPUT JOB DESCRIPTION:
\"\"\"
{jd_text}
\"\"\"
"""

_DEFAULT_INITIAL_INTERVIEW = """You are helping a candidate during a live technical interview.

You will receive short excerpts of what the interviewer said (as captions). When sent, reply with only the exact words the candidate should say next — concise, first person, no meta commentary.

Static resume and job context were already provided in earlier messages in this chat. Do not ask for them again.

Reply with one short sentence confirming you are ready, then stop."""

_DEFAULT_CHUNK_INTERVIEW = """You are generating what the candidate should say in an interview.

Hard rules (non-negotiable):
- Output only the final answer text the candidate should speak.
- No explanation, no analysis, no headings, no labels, no bullet list unless interviewer explicitly asked for a list.
- Use first person.
- Keep concise and natural, interview-ready tone.
- If multiple questions are present, answer them in asked order in compact paragraph form.
- If there is no clear question, output one short clarification question only.

Interviewer intent (cleaned):
\"\"\"
{cleaned_interviewer_intent}
\"\"\"
"""

_DEFAULTS: Dict[str, str] = {
    "resume_summary": _DEFAULT_RESUME_SUMMARY,
    "jd_summary": _DEFAULT_JD_SUMMARY,
    "initial_interview": _DEFAULT_INITIAL_INTERVIEW,
    "chunk_interview": _DEFAULT_CHUNK_INTERVIEW,
}


def template_path_for_key(key: str) -> Path:
    name = _TEMPLATE_FILES.get(key)
    if not name:
        raise ValueError(f"unknown template key: {key!r}")
    return app_root_directory() / name


def ensure_prompt_template_files() -> None:
    """Create each missing prompt .txt in the app root with the built-in default body."""
    for key, _fname in _TEMPLATE_FILES.items():
        path = template_path_for_key(key)
        if path.is_file():
            continue
        body = _DEFAULTS.get(key, "")
        path.write_text(body, encoding="utf-8", newline="\n")


def read_template_text(key: str) -> str:
    """Return on-disk template text for key, or empty string if missing/invalid."""
    if key not in _TEMPLATE_FILES:
        return ""
    path = template_path_for_key(key)
    if not path.is_file():
        return ""
    try:
        return path.read_text(encoding="utf-8")
    except OSError:
        return ""


def save_template_text(key: str, text: str) -> None:
    """Persist template body to the root .txt file for this key."""
    if key not in _TEMPLATE_FILES:
        raise ValueError(f"unknown template key: {key!r}")
    path = template_path_for_key(key)
    path.write_text(text or "", encoding="utf-8", newline="\n")


def load_prompt_templates_into_store(store: Any) -> None:
    """Reload all four templates from disk into the given PromptStore."""
    for key in _TEMPLATE_FILES:
        store.set_template(key, read_template_text(key))
