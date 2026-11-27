"""Detection views of a text.

Detectors never run on the raw text. They run on a *view* in which characters a reader does not see are removed and
look-alike letters are folded to ASCII, so obfuscation such as `ig​nore` or `іgnore` (Cyrillic і) is still found.
Every view character remembers the raw index it came from, so a match in the view maps back to the exact raw span
that has to be replaced. The raw text itself is never rewritten by normalisation.

The same rules are implemented in .NET (ClaimsAssist `TextNormalizer`). The shared corpus
(`dial/schema/guard-corpus.v1.json`) pins the confusables map so the two cannot drift.
"""

from __future__ import annotations

import json
import unicodedata
from dataclasses import dataclass
from functools import lru_cache
from pathlib import Path

# Characters that render as nothing (or only change direction) but still reach the model:
# zero-width space/non-joiner/joiner, LRM/RLM, word joiner, invisible operators, BOM, soft hyphen,
# Mongolian vowel separator, and the bidi embedding/override/isolate controls.
INVISIBLE = frozenset(
    [chr(c) for c in range(0x200B, 0x2010)]
    + [chr(c) for c in range(0x2060, 0x2065)]
    + [chr(c) for c in range(0x202A, 0x202F)]
    + [chr(c) for c in range(0x2066, 0x206A)]
    + ["﻿", "­", "᠎"]
)

# Look-alike letters (Cyrillic, Greek, Armenian, Latin variants) folded to ASCII. Pinned in the shared corpus.
CONFUSABLES: dict[str, str] = {
    # Cyrillic lower case
    "а": "a", "е": "e", "о": "o", "р": "p", "с": "c", "у": "y", "х": "x",
    "і": "i", "ј": "j", "ѕ": "s", "ԁ": "d", "ԛ": "q", "ԝ": "w", "һ": "h",
    "ӏ": "l", "к": "k",
    # Cyrillic upper case
    "А": "A", "В": "B", "Е": "E", "К": "K", "М": "M", "Н": "H", "О": "O",
    "Р": "P", "С": "C", "Т": "T", "Х": "X", "У": "Y", "І": "I", "Ј": "J",
    "Ѕ": "S", "Ӏ": "I",
    # Greek lower case
    "α": "a", "ο": "o", "ρ": "p", "ι": "i", "κ": "k", "ν": "v", "υ": "u",
    "χ": "x",
    # Greek upper case
    "Α": "A", "Β": "B", "Ε": "E", "Ζ": "Z", "Η": "H", "Ι": "I", "Κ": "K",
    "Μ": "M", "Ν": "N", "Ο": "O", "Ρ": "P", "Τ": "T", "Υ": "Y", "Χ": "X",
    # Armenian and Latin variants
    "օ": "o", "ս": "u", "ı": "i", "ȷ": "j",
}


@dataclass(frozen=True, slots=True)
class View:
    """A detection view: `text` plus, for every view character, the raw index it came from."""

    text: str
    raw_index: tuple[int, ...]
    raw_length: int

    def raw_start(self, view_start: int) -> int:
        return self.raw_index[view_start] if view_start < len(self.raw_index) else self.raw_length

    def raw_end(self, view_end: int) -> int:
        """Raw end (exclusive) of a view span ending at `view_end`. Invisible characters inside the span are covered;
        a raw character that expanded into several view characters is covered whole."""
        if view_end <= 0:
            return 0
        return self.raw_index[view_end - 1] + 1


@lru_cache(maxsize=4096)
def _fold_char(c: str) -> str:
    decomposed = unicodedata.normalize("NFKD", c)
    kept = "".join(d for d in decomposed if unicodedata.category(d) not in ("Mn", "Me"))
    return "".join(CONFUSABLES.get(d, d) for d in kept)


def strip_invisible_view(raw: str) -> View:
    """View for link detection: invisible characters removed, letters untouched (hosts keep their real code points)."""
    chars: list[str] = []
    index: list[int] = []
    for i, c in enumerate(raw):
        if c in INVISIBLE:
            continue
        chars.append(c)
        index.append(i)
    return View("".join(chars), tuple(index), len(raw))


def folded_view(raw: str) -> View:
    """View for PII, canary and injection detection: invisible characters removed, compatibility forms decomposed,
    combining marks dropped and look-alike letters folded to ASCII."""
    chars: list[str] = []
    index: list[int] = []
    for i, c in enumerate(raw):
        if c in INVISIBLE:
            continue
        for d in _fold_char(c):
            chars.append(d)
            index.append(i)
    return View("".join(chars), tuple(index), len(raw))


def detection_text(raw: str) -> str:
    """Folded view as a plain string with whitespace runs collapsed to one space (used to classify short segments)."""
    return " ".join(folded_view(raw).text.split())


def load_corpus(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))
