"""Quarantine of untrusted text (tool output, attachments, history stages).

This is a port of ClaimsAssist's `UntrustedContentGuard.Sanitize` + `Quarantine`, so the runtime guard and the agent's
ingestion guard give the same verdict on the same text (pinned by the shared corpus):

1. Sanitize: remove what a human reader never sees but a model does (HTML comments, invisible characters).
2. Quarantine: replace bracketed passages and sentences that address an AI system with instructions.
3. Whole-text pass: an instruction split across lines (which the sentence pass cannot see) replaces the whole text.

Classification runs on the folded detection view (`normalize.detection_text`), so look-alike letters, full-width
forms and zero-width characters do not hide an instruction. The replaced text keeps its original characters.
"""

from __future__ import annotations

import re
from collections.abc import Iterable
from functools import lru_cache

from .normalize import INVISIBLE, detection_text

PLACEHOLDER = "[removed: text addressed to an AI assistant]"

_HTML_COMMENT = re.compile(r"<!--.*?-->", re.S)
_INVISIBLE = re.compile("[" + "".join(re.escape(c) for c in sorted(INVISIBLE)) + "]")
_BRACKETED = re.compile(r"\[[^\[\]]{0,800}\]|\([^()]{0,800}\)")
_SENTENCE = re.compile(r"[^.!?\n]+[.!?]?")

_ADDRESSED_TO_AI = re.compile(
    r"\b(?:note|notes|instruction|instructions|message|attention|reminder)\b[^.\]\n]{0,40}\b(?:to|for)\b"
    r"[^.\]\n]{0,25}\b(?:AI|assistants?|LLMs?|chat ?bots?|language models?|copilots?|agents?)\b", re.I)
_OVERRIDE = re.compile(r"\b(?:ignore|disregard|forget)\b[^.\n]{0,40}\b(?:instructions?|rules|guidelines|prompts?)\b",
                       re.I)
_REVEAL = re.compile(
    r"\b(?:reveal|print|show|output|repeat|leak|disclose)\b[^.\n]{0,40}\b(?:system prompt|system message|"
    r"hidden instructions?|initial instructions?|developer message|configuration marker)\b", re.I)
_ROLE_TOKENS = re.compile(r"<\|im_start\|>|<\|im_end\|>|<\|system\|>|<\|endoftext\|>|\[/?INST\]|<<SYS>>", re.I)


@lru_cache(maxsize=64)
def _tool_command(tool_names: tuple[str, ...]) -> re.Pattern[str] | None:
    if not tool_names:
        return None
    names = "|".join(re.escape(n) for n in sorted(set(tool_names)))
    return re.compile(r"\b(?:call|invoke|run|execute|trigger)\b[^.\n]{0,25}\b(?:" + names + r")\b", re.I)


def is_instruction(segment: str, tool_names: Iterable[str] = ()) -> bool:
    """True if the segment addresses an AI system with instructions."""
    text = detection_text(segment)
    if (_ADDRESSED_TO_AI.search(text) or _OVERRIDE.search(text) or _REVEAL.search(text)
            or _ROLE_TOKENS.search(text)):
        return True
    tool = _tool_command(tuple(tool_names))
    return bool(tool and tool.search(text))


def sanitize(text: str) -> tuple[str, int]:
    """Remove HTML comments and invisible characters. Returns the trimmed text and the number of removals."""
    result, comments = _HTML_COMMENT.subn("", text)
    result, invisible = _INVISIBLE.subn("", result)
    return result.strip(), comments + invisible


def quarantine(text: str, tool_names: Iterable[str] = ()) -> tuple[str, int]:
    """Replace bracketed passages and sentences that are instructions to an AI. Returns text and replacement count."""
    names = tuple(tool_names)
    removed = 0

    def check(match: re.Match[str]) -> str:
        nonlocal removed
        value = match.group(0)
        if not is_instruction(value, names):
            return value
        removed += 1
        return PLACEHOLDER if value.startswith(("[", "(")) else " " + PLACEHOLDER

    result = _BRACKETED.sub(check, text)
    result = _SENTENCE.sub(check, result)

    # An instruction split across lines survives the sentence pass. Probe the whole text with line breaks collapsed
    # (sentence ends stay hard boundaries, and our own placeholders are blanked so they cannot trigger a match).
    probe = result.replace(PLACEHOLDER, " ").replace("!", ".").replace("?", ".")
    if is_instruction(probe, names):
        removed += 1
        result = PLACEHOLDER
    return result, removed


def clean_untrusted(text: str, tool_names: Iterable[str] = ()) -> tuple[str, int]:
    """Sanitize, then quarantine, then trim: the full treatment for untrusted text."""
    sanitized, hidden = sanitize(text)
    quarantined, instructions = quarantine(sanitized, tool_names)
    return quarantined.strip(), hidden + instructions
