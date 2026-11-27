"""Detectors and the span engine shared by block mode and streaming mode.

Every detector is a regular expression run on a detection view (see `normalize`). A match becomes a `Candidate`: a raw
span, the detector's name and the replacement text (None keeps the original, for an allowed link). Overlapping
candidates form a `Component` that is replaced as a unit, so nothing outside a replacement can leak a fragment of a
match (for example an e-mail address inside an allowed link removes the whole link).

Finality, which streaming depends on. A match is *final* when no text appended later can change it. The `regex`
module reports partial matches, but it also reports a match as complete when a longer one may still follow (an IBAN
followed by " " and then the end of the text can grow by " 00"). So each detector declares:

* `stop`: characters no part of the pattern can consume (and that satisfy its trailing `\\b`). Once one follows the
  match, the engine can never look past it.
* `peek`: how far past the match end the engine can possibly look (bounded patterns: their maximum length + 1).

A complete match is final when a stop character follows it or at least `peek` characters follow it. Every pattern is
bounded except bare URLs (`peek` infinite), which the streaming guard caps (see `textguard`).
"""

from __future__ import annotations

import math
from bisect import bisect_left
from dataclasses import dataclass, field

import regex

from .config import GuardConfig
from .links import LinkPolicy, classify_target
from .normalize import View, folded_view, strip_invisible_view

LINK_REMOVED = "[link removed]"
IMAGE_REMOVED = "[image removed]"
REDACTED = "[REDACTED]"

_TIMEOUT_SECONDS = 2.0  # backstop against pathological input; a timeout is a guard error (failure mode applies)

URL_CHAR_CLASS = r"[^\s<>\"'`]"
URL_STOP = regex.compile(r"[\s<>\"'`]")
# No boundary before the scheme or "www.": a URL glued to other text is not clickable, but its address is still
# readable and copyable, so it is judged like any other.
_URL = regex.compile(r"(?:https?|hxxps?)://" + URL_CHAR_CLASS + r"+|www\." + URL_CHAR_CLASS + r"+", regex.I)
_TRAILING = ".,;:!?'\"*_~"


@dataclass(frozen=True, slots=True)
class _Pii:
    pattern: regex.Pattern[str]
    stop: regex.Pattern[str]
    peek: int
    # The national phone form must not continue a digit run, unless the digit before it is itself being redacted
    # (then the number would stand alone, readable, right after the marker).
    not_after_kept_digit: bool = False
    # More patterns of the same kind, each searched on its own. With partial=True the regex module prefers a complete
    # match at a later start over a partial one at an earlier start; a form that another alternative completes one
    # position later ("0049 151" vs "049 151") would then never be held back while streaming.
    also: tuple[regex.Pattern[str], ...] = ()


# No PII pattern depends on its neighbours (no \b): a redaction marker next to a value must not change whether the
# value matches, and a value run straight into other text ("...example.comDE89...") must still be found. Overlapping
# matches merge into one redaction (see Component).
_PII: dict[str, _Pii] = {
    # An IBAN run into other letters or digits ("...51DE8937...") is still an IBAN.
    "iban": _Pii(regex.compile(r"[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,3})?"),
                 regex.compile(r"[^A-Z0-9 ]"), 45),
    "email": _Pii(regex.compile(r"[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,253}\.[A-Za-z]{2,24}"),
                  regex.compile(r"[^A-Za-z0-9._%+@-]"), 345),
    # International numbers: "+" or the "00" dialling prefix ("0049 151 ...", "001 415 ..."). "+" may follow anything
    # but "+"; the "00" form follows the national rule below (not right after a digit that stays in the text, so
    # "1000123456" is no phone number). A decimal such as "0.0012345678" is redacted too: telling it apart would need
    # the digit before the separator, whose fate (kept or replaced) a pattern cannot see. Listed before phone_de: the
    # second pass decides in this order, and a national number glued to an international one must see that its first
    # digit is being replaced.
    "phone_intl": _Pii(regex.compile(r"(?:(?<!\+)\+|00)[1-9](?:[ .-]?\d){6,14}"),
                       regex.compile(r"[^0-9 .+-]"), 32, not_after_kept_digit=True),
    # "+49" may follow anything but "+" (a delimiter of its own). The national form "0..." right after a digit that
    # stays in the text is not a phone number: claim ids such as CLM-2026-0142 must survive (corpus case p-none).
    # "0049..." is a separate pattern (see _Pii.also): inside it, the national form completes one position later.
    "phone_de": _Pii(regex.compile(r"(?:(?<!\+)\+49 ?|0)[1-9]\d{1,4}[ /-]?\d{3,9}"),
                     regex.compile(r"[^0-9 +/-]"), 21, not_after_kept_digit=True,
                     also=(regex.compile(r"0049 ?[1-9]\d{1,4}[ /-]?\d{3,9}"),)),
    # No digit boundaries: "14.03.19860" still shows a readable date. A fixed-length pattern cannot grow, so a date is
    # final as soon as one character follows it.
    "date_de": _Pii(regex.compile(r"\d{2}\.\d{2}\.\d{4}"), regex.compile(r"[^0-9.]"), 1),
    # Card shapes only: 13 to 19 digits in a row, 4-4-4-x groups, or 4-6-4/5 groups (Amex, Diners). A list of numbers
    # separated by spaces ("100 200 300 ...") is not a card. No boundaries, overlapped: a card inside a longer digit
    # run is still found, and each start is validated once (windows starting there).
    "card": _Pii(regex.compile(r"\d{13,19}|\d{4}[ -]\d{4}[ -]\d{4}[ -]\d{1,7}|\d{4}[ -]\d{6}[ -]\d{4,5}"),
                 regex.compile(r"[^0-9 -]"), 24),
}


def _luhn_ok(digits: str) -> bool:
    total = 0
    for i, ch in enumerate(reversed(digits)):
        d = ord(ch) - 48
        if i % 2 == 1:
            d *= 2
            if d > 9:
                d -= 9
        total += d
    return total % 10 == 0


def _pii_replacement(kind: str, matched: str) -> str | None:
    """Replacement marker for a PII match, or None if validation says it is not PII."""
    if kind == "email":
        return "[EMAIL]"
    if kind == "iban":
        return "[IBAN ending " + matched.replace(" ", "")[-4:] + "]"
    if kind in ("phone_de", "phone_intl"):
        return "[PHONE]"
    if kind == "date_de":
        return "[DATE]"
    if kind == "card":
        return _card_replacement(matched)
    raise AssertionError(kind)


def _card_replacement(matched: str) -> str | None:
    """A card match is a card when its first 13 to 19 digits pass the Luhn check. Windows starting later are the
    matches found at later starts (the search is overlapped), so every window is checked exactly once, and a digit
    glued to the end ("4111 1111 1111 11110") does not hide the card in front of it. The whole match is replaced."""
    digits = "".join(ch for ch in matched if ch.isdigit())
    for length in range(min(19, len(digits)), 12, -1):
        if _luhn_ok(digits[:length]):
            return "[CARD ending " + digits[length - 4:length] + "]"
    return None


def _is_final(match: regex.Match[str], text: str, stop: regex.Pattern[str] | None, peek: float) -> bool:
    if match.partial:
        return False
    end = match.end()
    if len(text) - end >= peek:
        return True
    return stop is not None and stop.search(text, end) is not None


@dataclass(slots=True)
class Candidate:
    start: int
    end: int
    detector: str
    kind: str  # "pii", "link" or "canary"
    replacement: str | None  # None keeps the original text
    final: bool
    bare_url: bool = False  # the only unbounded detector, capped by the streaming guard
    target: tuple[int, int] | None = None  # markdown link or image: raw span of its target


@dataclass(slots=True)
class Component:
    members: list[Candidate] = field(default_factory=list)
    start: int = 0
    end: int = 0

    def add(self, c: Candidate) -> None:
        if not self.members:
            self.start, self.end = c.start, c.end
        else:
            self.start, self.end = min(self.start, c.start), max(self.end, c.end)
        self.members.append(c)

    @property
    def final(self) -> bool:
        return all(m.final for m in self.members)

    @property
    def canary(self) -> bool:
        return any(m.kind == "canary" for m in self.members)

    @property
    def has_link(self) -> bool:
        return any(m.kind == "link" for m in self.members)

    def resolve(self) -> tuple[str | None, list[str]]:
        """(replacement or None to keep the original, detectors to record). Only called on final components."""
        if len(self.members) == 1:
            m = self.members[0]
            return m.replacement, ([m.detector] if m.replacement is not None else [])
        acting = [m for m in self.members if m.replacement is not None]
        if not acting:
            return None, []  # allowed links overlapping each other
        # A markdown link or image whose target is the bare URL found inside it: judge it as one link, which keeps
        # the label of a refused link and says "image removed" for a refused image.
        md = [m for m in self.members if m.target is not None]
        if len(md) == 1 and md[0].start == self.start and md[0].end == self.end:
            outer, inner = md[0], [m for m in self.members if m is not md[0]]
            assert outer.target is not None
            # The bare URL starts inside the target; it may end on the link's closing ")" (wiki/Foo_(bar)).
            if all(m.bare_url and outer.target[0] <= m.start < outer.target[1] and m.end <= outer.end for m in inner):
                if outer.replacement is not None:
                    return outer.replacement, [outer.detector]
                return LINK_REMOVED, sorted({m.detector for m in acting})  # the two readings disagree: refuse
        detectors = sorted({m.detector for m in acting})
        if self.has_link:
            # PII inside or next to an allowed link removes the link too: never keep part of a URL around a redaction.
            if any(m.kind == "link" and m.replacement is None for m in self.members) and any(
                    m.kind == "pii" for m in acting):
                detectors.append("link.contains_pii")
            return LINK_REMOVED, detectors
        for m in acting:  # PII only: a match covering the whole component wins, else a generic marker
            if m.start == self.start and m.end == self.end:
                return m.replacement, [m.detector]
        return REDACTED, detectors


def components(cands: list[Candidate]) -> list[Component]:
    out: list[Component] = []
    for c in sorted(cands, key=lambda c: (c.start, -c.end)):
        if out and c.start < out[-1].end:
            out[-1].add(c)
        else:
            comp = Component()
            comp.add(c)
            out.append(comp)
    return out


class Scanner:
    """All detectors of one policy. Stateless."""

    def __init__(self, config: GuardConfig, policy: LinkPolicy):
        self.max_link_length = config.max_link_length
        self.policy = policy
        # The table's order, not the configuration's: the second pass depends on it (see phone_intl).
        self.pii = [(kind, det) for kind, det in _PII.items() if kind in set(config.pii)]
        canaries = sorted({folded_view(c).text for c in config.canaries if folded_view(c).text}, key=len, reverse=True)
        if canaries:
            self.canary = regex.compile("|".join(regex.escape(c) for c in canaries), regex.I)
            chars = {ch for c in canaries for ch in (c.lower() + c.upper())}
            self.canary_stop = regex.compile("[^" + "".join(regex.escape(ch) for ch in sorted(chars)) + "]")
            self.canary_peek = max(len(c) for c in canaries) + 1
        else:
            self.canary = None
        t = config.max_link_length
        # The target ends at the first ")" that closes the link (lazy), so targets with parentheses such as
        # javascript:alert(1) or wiki/Foo_(bar) are still recognised and judged.
        self.markdown = regex.compile(
            r"(!?)\[([^\[\]\n]{0,300})\]\(\s{0,10}(<[^<>\n]{1,%d}>|[^\s<>]{1,%d}?)(?:\s{1,10}\"[^\"\n]{0,200}\")?"
            r"\s{0,10}\)" % (t, t))

    @staticmethod
    def _iter(pattern: regex.Pattern[str], view: View, raw_base: int, final: bool, overlapped: bool = False):
        pos = bisect_left(view.raw_index, raw_base)
        return pattern.finditer(view.text, pos, overlapped=overlapped, partial=not final, timeout=_TIMEOUT_SECONDS)

    def candidates(self, raw: str, base: int, final: bool, boundary_redacted: bool = False) -> list[Candidate]:
        """Candidates starting at or after raw index `base` (earlier text is already released).

        With `final=True` the text is complete: no partial matches, every match is final. `boundary_redacted` says
        whether the last visible character before `base` belongs to released text that was replaced."""
        out: list[Candidate] = []
        folded = folded_view(raw)
        visible = strip_invisible_view(raw)

        for kind, det in self.pii:
            if det.not_after_kept_digit:
                continue  # second pass, once every other match is known
            out.extend(self._pii_matches(kind, det, folded, base, final))

        if self.canary is not None:
            for m in self._iter(self.canary, folded, base, final):
                if m.start() == m.end():
                    continue
                is_final = final or _is_final(m, folded.text, self.canary_stop, self.canary_peek)
                out.append(Candidate(folded.raw_start(m.start()), folded.raw_end(m.end()), "canary", "canary",
                                     None, is_final))

        for m in self._iter(self.markdown, visible, base, final):
            if m.start() == m.end():
                continue
            is_final = final or _is_final(m, visible.text, None, 1)
            target = (visible.raw_start(m.start(3)), visible.raw_end(m.end(3)))
            replacement, detector = None, "link.allowed"
            if is_final:
                target_raw = raw[target[0]:target[1]]
                if target_raw.startswith("<") and target_raw.endswith(">"):
                    target_raw = target_raw[1:-1]
                refusal = classify_target(target_raw, self.policy)
                if refusal is not None:
                    detector = refusal
                    if m.group(1) == "!":
                        replacement = IMAGE_REMOVED
                    else:
                        label = raw[visible.raw_start(m.start(2)):visible.raw_end(m.end(2))] \
                            if m.end(2) > m.start(2) else ""
                        replacement = label if label.strip() else LINK_REMOVED
            out.append(Candidate(visible.raw_start(m.start()), visible.raw_end(m.end()), detector, "link",
                                 replacement, is_final, target=target))

        for m in self._iter(_URL, visible, base, final):
            if m.start() == m.end():
                continue
            is_final = final or _is_final(m, visible.text, URL_STOP, math.inf)
            vstart, vend = m.start(), m.end()
            replacement, detector = None, "link.allowed"
            if is_final:
                # Length is measured on the raw run, the measure the streaming cap uses.
                if visible.raw_end(vend) - visible.raw_start(vstart) > self.max_link_length:
                    replacement, detector = LINK_REMOVED, "link.too_long"
                else:
                    text = m.group()
                    prefix = 4 if text[:4].lower() == "www." else text.index("://") + 3
                    while len(text) > prefix and text[-1] in _TRAILING + ")]":
                        last = text[-1]
                        if last == ")" and text.count("(") >= text.count(")"):
                            break
                        if last == "]" and text.count("[") >= text.count("]"):
                            break
                        text = text[:-1]
                    if len(text) == prefix:
                        continue  # only the URL's own scheme or "www." left: nothing to open
                    vend = vstart + len(text)
                    refusal = classify_target(raw[visible.raw_start(vstart):visible.raw_end(vend)], self.policy)
                    if refusal is not None:
                        replacement, detector = LINK_REMOVED, refusal
            out.append(Candidate(visible.raw_start(vstart), visible.raw_end(vend), detector, "link", replacement,
                                 is_final, bare_url=True))

        # National phone numbers: a match right after a digit counts only if that digit is being replaced (or may
        # still be), because then the number would stand readable next to the marker.
        fpos = bisect_left(folded.raw_index, base)
        first_pass = components(out)
        starts = [c.start for c in first_pass]
        accepted: list[Candidate] = []
        for kind, det in self.pii:
            if not det.not_after_kept_digit:
                continue
            for cand, vstart in self._pii_matches(kind, det, folded, base, final, with_view_start=True):
                if vstart > 0 and folded.text[vstart - 1].isdigit() and folded.text[vstart] == "0":
                    if vstart == fpos:
                        if not boundary_redacted:
                            continue
                    else:
                        prev = folded.raw_start(vstart - 1)
                        i = bisect_left(starts, prev + 1) - 1
                        cover = first_pass[i] if i >= 0 and first_pass[i].start <= prev < first_pass[i].end else None
                        if cover is not None and cover.final and not cover.canary and cover.resolve()[0] is None:
                            # Kept as is (an allowed link), unless a phone number accepted in this pass overlaps it:
                            # the union is then replaced.
                            hit = [p for p in accepted if p.start < cover.end and cover.start < p.end]
                            replaced, still_open = bool(hit), any(not p.final for p in hit)
                        elif cover is not None:
                            replaced, still_open = True, not cover.final
                        else:  # an earlier phone number of this pass, glued to this one
                            hit = [p for p in accepted if p.start <= prev < p.end]
                            replaced, still_open = bool(hit), any(not p.final for p in hit)
                        if not replaced:
                            continue
                        if still_open:
                            cand.final, cand.replacement = False, None
                accepted.append(cand)
        out.extend(accepted)
        return out

    def _pii_matches(self, kind: str, det: _Pii, folded: View, base: int, final: bool, with_view_start=False):
        # Overlapped: a match that fails validation, or ends inside the next value, must not hide a match that starts
        # within it (two e-mail addresses run together; a Luhn-failing run in front of a card number).
        found = []
        for pattern in (det.pattern, *det.also):
            for m in self._iter(pattern, folded, base, final, overlapped=True):
                if m.start() == m.end():
                    continue
                is_final = final or _is_final(m, folded.text, det.stop, det.peek)
                replacement = _pii_replacement(kind, m.group()) if is_final else None
                if is_final and replacement is None:
                    continue  # failed validation (e.g. Luhn): not PII
                cand = Candidate(folded.raw_start(m.start()), folded.raw_end(m.end()), "pii." + kind, "pii",
                                 replacement, is_final)
                found.append((cand, m.start()) if with_view_start else cand)
        if det.also:
            found.sort(key=lambda f: f[1] if with_view_start else f.start)  # one position order, as from one pattern
        return found
