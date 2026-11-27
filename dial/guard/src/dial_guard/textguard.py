"""Streaming guard for one text field. Block mode is the same guard fed the whole text at once.

The guard keeps unreleased text in a buffer and releases it up to the first match that more text could still change
(see `detectors`). That makes the output independent of how the text is split into chunks: for any split, the
concatenated releases equal the block-mode result. The property tests check exactly that.

Bare URLs are the only unbounded matches. When an unterminated URL makes the buffer exceed its cap, the URL is
already longer than any allowed link, so it will be removed whatever follows: the guard releases the marker at once
and drops the rest of the URL as it streams in. Block mode removes such a link whole, so both modes agree.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass

from .detectors import LINK_REMOVED, URL_STOP, Scanner, components
from .errors import GuardInternalError
from .normalize import folded_view

CONTEXT_CHARS = 16  # text kept before the buffer so look-behinds see the real preceding characters
CAP_SLACK = 600  # room around a link target for a markdown label, title and syntax
# A run made only of PII-shaped matches (no link) is short: the longest single match is an e-mail address
# (345 characters). Longer runs are chains of overlapping matches in a digit or code blob; holding and rescanning
# them would cost quadratic CPU, so they are a guard error in both modes.
PII_RUN_CAP = 512
SLICE_CHARS = 2048  # largest piece scanned at once


@dataclass(frozen=True, slots=True)
class Emission:
    text: str
    blocked: bool


class TextGuard:
    def __init__(self, scanner: Scanner) -> None:
        self._scanner = scanner
        self._pending = ""
        self._context = ""
        self._skip = False
        self._blocked = False
        self._boundary_redacted = False  # the last visible released character belongs to a replacement
        self.findings: Counter[str] = Counter()
        self.cap = scanner.max_link_length + CAP_SLACK

    @property
    def blocked(self) -> bool:
        return self._blocked

    @property
    def holding(self) -> int:
        """Number of raw characters currently held back."""
        return len(self._pending)

    def feed(self, piece: str) -> Emission:
        """Takes the next piece of text. A large piece is consumed in slices: slicing is just a finer chunking, so the
        output is the same (split invariance), while the work per scan stays bounded and a pathological run is
        rejected after the first slice instead of after a scan of the whole piece."""
        if self._blocked:
            return Emission("", True)
        out = []
        for i in range(0, len(piece), SLICE_CHARS):
            self._pending += piece[i:i + SLICE_CHARS]
            out.append(self._run(final=False))
            if self._blocked:
                break
        return Emission("".join(out), self._blocked)

    def finish(self) -> Emission:
        if self._blocked:
            return Emission("", True)
        return Emission(self._run(final=True), self._blocked)

    def _overlong_url(self, comp) -> bool:
        return any(m.bare_url and m.end - m.start > self._scanner.max_link_length for m in comp.members)

    def _check_run(self, comp, still_open: bool = False) -> None:
        """Same rule in block mode and streaming, whatever the chunking: a run of overlapping matches longer than the
        guard checks raises. Runs of PII-shaped matches only are capped at PII_RUN_CAP, runs with links at the
        buffer cap; an overlong bare URL is the exception (it is removed whole). For a run that is still open only its
        final matches count: they cannot change any more, so block mode will see at least that much."""
        if still_open:
            ends = [m.end for m in comp.members if m.final]
            length = (max(ends) - comp.start) if ends else 0
        else:
            length = comp.end - comp.start
        limit = self.cap if comp.has_link else PII_RUN_CAP
        if length > limit and not self._overlong_url(comp):
            raise GuardInternalError("a run of overlapping matches is longer than the guard checks")

    def take_raw(self) -> str:
        """Fail-open: hand back the unreleased raw text unchecked and stop holding it."""
        raw, self._pending, self._skip = self._pending, "", False
        return raw

    def _run(self, final: bool) -> str:
        out: list[str] = []
        while True:
            if self._skip:
                stop = URL_STOP.search(self._pending)
                if stop is None:
                    self._context = (self._context + self._pending)[-CONTEXT_CHARS:]
                    self._pending = ""
                    return "".join(out)
                self._context = (self._context + self._pending[:stop.start()])[-CONTEXT_CHARS:]
                self._pending = self._pending[stop.start():]
                self._skip = False

            text = self._context + self._pending
            base = len(self._context)
            comps = components(self._scanner.candidates(text, base, final, self._boundary_redacted))

            cut = len(text)
            if not final:
                for comp in comps:
                    if not comp.final:
                        cut = min(cut, comp.start)
            for comp in comps:
                if comp.start < cut < comp.end:
                    cut = comp.start

            pos = base
            for comp in comps:
                if comp.start < base:
                    raise GuardInternalError("a match crosses text that was already released")
                if comp.end > cut:
                    break
                self._check_run(comp)
                out.append(text[pos:comp.start])
                if comp.canary:
                    self.findings["canary"] += 1
                    self._blocked = True
                    self._pending = ""
                    return "".join(out)
                if _visible(text[pos:comp.start]):
                    self._boundary_redacted = False
                replacement, detectors = comp.resolve()
                out.append(text[comp.start:comp.end] if replacement is None else replacement)
                self.findings.update(detectors)
                self._boundary_redacted = replacement is not None
                pos = comp.end
            out.append(text[pos:cut])
            if _visible(text[pos:cut]):
                self._boundary_redacted = False

            self._context = text[max(0, cut - CONTEXT_CHARS):cut]
            self._pending = text[cut:]
            blocking = next((c for c in comps if c.start == cut and not c.final), None)
            if blocking is not None and not final:
                self._check_run(blocking, still_open=True)
            url = None if blocking is None else next(
                (m for m in blocking.members if m.bare_url and not m.final), None)
            if final or url is None or url.end - url.start <= self._scanner.max_link_length:
                # Held text is bounded: final matches by the run check, open matches by their pattern length, and an
                # open bare URL by max_link_length (beyond it, the branch below takes over).
                return "".join(out)
            out.append(LINK_REMOVED)
            self.findings["link.too_long"] += 1
            self._context = text[-CONTEXT_CHARS:]
            self._pending = ""
            self._skip = True
            self._boundary_redacted = False  # a skipped URL always ends at whitespace or a quote


def _visible(segment: str) -> bool:
    """True if the segment has a character that shows up in the folded detection view."""
    return bool(segment) and bool(folded_view(segment).text)


def guard_text(scanner: Scanner, text: str) -> tuple[str, bool, Counter[str]]:
    """Guards a complete text (production path): fed in slices, then finished. (text, blocked, findings)."""
    guard = TextGuard(scanner)
    emitted = guard.feed(text)
    rest = guard.finish()
    return emitted.text + rest.text, guard.blocked, guard.findings


def guard_block(scanner: Scanner, text: str) -> tuple[str, bool, Counter[str]]:
    """Reference block mode, one final scan of the whole text: (guarded text, blocked by a canary, findings).

    The property tests compare every streaming split against this. It scans the whole text at once, so production
    code uses guard_text, which gives the same result with bounded work per scan."""
    guard = TextGuard(scanner)
    guard._pending = text  # noqa: SLF001 - the one place the reference enters the buffer directly
    emitted = guard.finish()
    return emitted.text, guard.blocked, guard.findings
