"""Shared corpus (dial/schema/guard-corpus.v1.json): the contract between this guard and the .NET guards."""

import json

import pytest

from conftest import SCHEMA_DIR
from dial_guard.config import GuardConfig
from dial_guard.detectors import Scanner
from dial_guard.links import LinkPolicy
from dial_guard.normalize import CONFUSABLES, INVISIBLE
from dial_guard.quarantine import PLACEHOLDER, clean_untrusted
from dial_guard.textguard import TextGuard, guard_block

CORPUS = json.loads((SCHEMA_DIR / "guard-corpus.v1.json").read_text(encoding="utf-8"))
ALLOWED = CORPUS["allowed_link_hosts"]


def test_corpus_pins_the_normalisation_tables():
    assert CORPUS["placeholder"] == PLACEHOLDER
    assert CORPUS["invisible"] == sorted(f"U+{ord(c):04X}" for c in INVISIBLE)
    assert CORPUS["confusables"] == {f"U+{ord(k):04X}": v for k, v in sorted(CONFUSABLES.items())}


@pytest.mark.parametrize("case", CORPUS["quarantine"], ids=lambda c: c["id"])
def test_quarantine(case):
    cleaned, removed = clean_untrusted(case["text"], CORPUS["tool_names"])
    assert (removed > 0) == case["expect_changed"]
    assert cleaned == case["expect_text"]


@pytest.mark.parametrize("case", CORPUS["links"], ids=lambda c: c["id"])
def test_links(case):
    scanner = Scanner(GuardConfig(allowed_link_hosts=ALLOWED, pii=[]), LinkPolicy(tuple(ALLOWED)))
    _, _, findings = guard_block(scanner, case["text"])
    assert any(d.startswith("link.") for d in findings) == case["expect_disallowed"]


@pytest.mark.parametrize("case", CORPUS["pii"], ids=lambda c: c["id"])
def test_pii(case):
    scanner = Scanner(GuardConfig(allowed_link_hosts=ALLOWED, pii=["email", "iban", "phone_de", "date_de"]),
                      LinkPolicy(tuple(ALLOWED)))
    text, _, findings = guard_block(scanner, case["text"])
    assert sorted(d.split(".", 1)[1] for d in findings) == case["expect_kinds"]
    assert text == case["expect_text"]
    # Streaming one character at a time gives the same text.
    guard = TextGuard(scanner)
    streamed = "".join(guard.feed(ch).text for ch in case["text"]) + guard.finish().text
    assert streamed == case["expect_text"]
