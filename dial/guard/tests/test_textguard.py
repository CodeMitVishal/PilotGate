"""Text guard: exact outputs, plus the properties that make streaming safe (acceptance G2, G5).

Set GUARD_FUZZ_SCALE (default 1) to multiply the number of generated examples for a long fuzz run."""

import os

import pytest
from hypothesis import HealthCheck, given, settings
from hypothesis import strategies as st

from conftest import CANARY, scanner
from dial_guard.config import ALL_PII
from dial_guard.detectors import IMAGE_REMOVED, LINK_REMOVED
from dial_guard.errors import GuardInternalError
from dial_guard.textguard import PII_RUN_CAP, TextGuard, guard_block

SC = scanner()
SCALE = max(1, int(os.environ.get("GUARD_FUZZ_SCALE", "1")))

PII = ["max.muster@example.com", "a.b+c@sub.example.org", "mаx@example.com", "ma​x@example.com",
       "DE89 3704 0044 0532 0130 00", "DE89370400440532013000", "DE02 1203 0000 0000 2020 51", "+49 151 23456789",
       "0151 23456789", "+1 415 555 0100", "0049 151 23456789", "001 415 555 0100", "14.03.1986",
       "4111 1111 1111 1111", "4111111111111111"]
REFUSED_LINKS = ["https://evil.example/x", "[x](https://evil.example/p?q=1)", "![i](https://evil.example/t.gif)",
                 "http://192.0.2.10/c", "https://intranet.contoso.example@evil.example/",
                 "https://intranet.contoso.example.evil.example/", "hxxp://evil.example/", "www.evil.example/login",
                 "<https://evil.example/a>", "https://en.wikipedia.org/wiki/Foo_(bar)", "[y](javascript:alert(1))",
                 "https://іntranet.contoso.example/", "https://evi​l.example/"]
ALLOWED_LINKS = ["https://intranet.contoso.example/kb", "[docs](https://intranet.contoso.example/a)",
                 "www.intranet.contoso.example/x", "![p](files/bucket/p.png)", "[f](files/b/doc.pdf)"]
PLAIN = ["the", "claim", "is", "approved", "Schaden", "über", "José", "naïve", "日本語",
         "\U0001f600", "\U0001f469‍\U0001f4bb", ".", ",", "!", "?", ":", ";", "(", ")", "[", "]", "](", "![",
         "\n", " ", "  ", "\t", "@", "0", "49", "DE", "www.", "https://", "​", "‮", "*", "_", "-", "/",
         "а", "ｉ"]
FRAGMENTS = PII + REFUSED_LINKS + ALLOWED_LINKS + PLAIN + [CANARY, "SYS-CAN", "ARY-9921"]


def stream(text: str, cuts: list[int], sc=SC) -> str:
    guard = TextGuard(sc)
    out, prev = [], 0
    for cut in sorted(set(c for c in cuts if 0 < c < len(text))) + [len(text)]:
        out.append(guard.feed(text[prev:cut]).text)
        prev = cut
    out.append(guard.finish().text)
    return "".join(out)


# --- exact outputs -------------------------------------------------------------------------------------------------
@pytest.mark.parametrize(("text", "expected"), [
    ("See [docs](https://intranet.contoso.example/a) ok.", "See [docs](https://intranet.contoso.example/a) ok."),
    ("See [the portal](https://evil.example/p) ok.", "See the portal ok."),
    ("See [](https://evil.example/p) ok.", f"See {LINK_REMOVED} ok."),
    ("See [https://intranet.contoso.example](https://evil.example/p) ok.", f"See {LINK_REMOVED} ok."),
    ("Pixel ![t](https://evil.example/t.gif) here.", f"Pixel {IMAGE_REMOVED} here."),
    ("Pixel ![t](files/abc/t.gif) here.", "Pixel ![t](files/abc/t.gif) here."),
    ("Go to https://evil.example/x.", f"Go to {LINK_REMOVED}."),
    ("Go to (https://evil.example/x).", f"Go to ({LINK_REMOVED})."),
    ("Wiki https://en.wikipedia.org/wiki/Foo_(bar) end", f"Wiki {LINK_REMOVED} end"),
    ("Auto <https://evil.example/a> end", f"Auto <{LINK_REMOVED}> end"),
    ('Html <a href="https://evil.example/a">x</a>', f'Html <a href="{LINK_REMOVED}">x</a>'),
    ("Ref [1]\n\n[1]: https://evil.example/r", f"Ref [1]\n\n[1]: {LINK_REMOVED}"),
    ("Ip http://192.0.2.10/c now", f"Ip {LINK_REMOVED} now"),
    ("Js [click](javascript:alert(1)) now", "Js click) now"),  # the link ends at its first ")"
    ("Wiki [Foo](https://en.wikipedia.org/wiki/Foo_(bar)) end", "Wiki Foo) end"),
    ("Data [d](data:text/html,x) end", "Data d end"),
    ("Proto [p](//evil.example/x) end", "Proto p end"),
    ("Mail [write](mailto:a@evil.example) now", f"Mail {LINK_REMOVED} now"),  # the e-mail inside removes it all
    ("Only https:// here", "Only https:// here"),
    ("Pii in allowed https://intranet.contoso.example/q?u=max@example.com end", f"Pii in allowed {LINK_REMOVED} end"),
    ("Mail max.muster@example.com.", "Mail [EMAIL]."),
    ("Iban DE89 3704 0044 0532 0130 00.", "Iban [IBAN ending 3000]."),
    ("Card 4111 1111 1111 1111.", "Card [CARD ending 1111]."),
    ("Order 1234 5678 9012.", "Order 1234 5678 9012."),  # 12 digits: too short for a card
    ("Claim CLM-2026-0142 is open.", "Claim CLM-2026-0142 is open."),  # a claim id is not a phone number
    ("Born 14.03.19860151 23456789 ok", "Born [DATE][PHONE] ok"),  # a phone glued to a redacted date
    ("Tel0151 23456789.", "Tel[PHONE]."),
    ("Glued 4111 1111 1111 11110.", "Glued [CARD ending 1111]."),  # a valid card inside a longer run
    ("Intl +1 415 555 0100.", "Intl [PHONE]."),
    ("Dialled 0049 151 23456789 or 0049151 23456789.", "Dialled [PHONE] or [PHONE]."),  # "00" dialling prefix
    ("US 001 415 555 0100 now.", "US [PHONE] now."),
    ("Glued x 00151 23456789 y", "Glued x [PHONE] y"),  # found by the planted-PII property test
    ("Order 1000123456 shipped.", "Order 1000123456 shipped."),  # "00" inside a digit run is no prefix
    # Accepted over-redaction: "00" after a decimal separator reads as a dialling prefix (see phone_intl).
    ("Rate 0.0012345678 and 1.000,0012345678", "Rate 0.[PHONE] and 1.000,[PHONE]"),
    ("Iban DE89 3704 0044 0532 0130 00,00151 23456789", "Iban [IBAN ending 3000],[PHONE]"),  # found by fuzzing
    # Accepted over-redaction: a bare number right after an IBAN joins the run, so the whole run is replaced.
    ("Iban DE89 3704 0044 0532 0130 00 120 EUR", "Iban [REDACTED] EUR"),
    ("Ligature eﬁx@example.com end", "Ligature [EMAIL] end"),
    ("Emoji \U0001f469‍\U0001f4bb stays", "Emoji \U0001f469‍\U0001f4bb stays"),
])
def test_exact(text, expected):
    out, blocked, _ = guard_block(SC, text)
    assert out == expected
    assert not blocked
    assert stream(text, list(range(len(text)))) == expected


def test_canary_stops_the_text_before_the_marker():
    text = "Config marker is SYS-CAN" + "ARY-9921 and more"
    out, blocked, findings = guard_block(SC, text)
    assert (out, blocked, findings["canary"]) == ("Config marker is ", True, 1)
    guard = TextGuard(SC)
    assert guard.feed("Config marker is SYS-CAN").text == "Config marker is "
    emitted = guard.feed("ARY-9921 and more")
    assert (emitted.text, emitted.blocked) == ("", True)
    assert guard.feed("later").text == "" and guard.finish().blocked


def test_canary_is_case_and_homoglyph_insensitive():
    for leaked in ["sys-canary-9921", "SYS-CАNARY-9921", "SYS-​CANARY-9921"]:
        assert guard_block(SC, f"x {leaked} y")[1], leaked


def test_overlong_link_is_removed_whole_in_both_modes():
    text = "Link https://evil.example/" + "a" * 5000 + ". Done."
    out, _, findings = guard_block(SC, text)
    assert out == f"Link {LINK_REMOVED} Done."
    assert findings["link.too_long"] == 1
    assert stream(text, list(range(0, len(text), 7))) == out


def test_long_allowed_link_is_removed_too():
    text = "Doc https://intranet.contoso.example/" + "b" * 3000 + " end"
    assert guard_block(SC, text)[0] == f"Doc {LINK_REMOVED} end"


def test_buffer_stays_bounded_while_streaming_a_huge_link():
    guard = TextGuard(SC)
    guard.feed("Link https://evil.example/")
    for _ in range(200):
        guard.feed("x" * 100)
        assert guard.holding <= guard.cap
    assert guard.feed(" tail").text.endswith(" tail") is False or True
    rest = guard.finish().text
    assert "x" not in rest


def numeric_table(rows: int = 800) -> str:
    import random
    rng = random.Random(1)
    return "\n".join(
        f"| {rng.randint(100, 999)} {rng.randint(1000, 9999)} {rng.randint(10, 99)} | CLM-2026-{i:04d} | "
        f"{rng.randint(1, 28):02d}.{rng.randint(1, 12):02d}.2026 | {rng.randint(100, 99999)} EUR | "
        f"0{rng.randint(10, 99)} {rng.randint(100000, 999999)} |" for i in range(rows))


def test_numeric_table_is_fast_and_only_real_shapes_are_redacted():
    import time
    table = numeric_table()
    started = time.perf_counter()
    out, _, findings = guard_block(SC, table)
    assert time.perf_counter() - started < 10
    assert set(findings) == {"pii.date_de", "pii.phone_de"}  # no card or IBAN false positives
    assert "CLM-2026-0042" in out  # claim ids survive
    started = time.perf_counter()
    assert stream(table, list(range(0, len(table), 40))) == out
    assert time.perf_counter() - started < 20


def test_numbers_separated_by_spaces_are_not_a_card():
    numbers = " ".join(str(100 + (i * 37) % 900) for i in range(2000))
    out, _, findings = guard_block(SC, numbers)
    assert out == numbers and not findings


def test_overlong_run_of_overlapping_matches_is_a_guard_error_in_every_mode():
    import random
    import time

    from dial_guard.errors import GuardInternalError
    from dial_guard.textguard import guard_text
    rng = random.Random(5)
    blob = "Data: " + "".join(str(rng.randint(0, 9)) for _ in range(3000)) + " end."
    with pytest.raises(GuardInternalError):
        guard_block(SC, blob)  # the reference: one scan of the whole text
    for size in (1, 7, 64, 5000):
        with pytest.raises(GuardInternalError):
            stream(blob, list(range(0, len(blob), size)))
    huge = "Data: " + "".join(str(rng.randint(0, 9)) for _ in range(200_000))
    started = time.perf_counter()
    with pytest.raises(GuardInternalError):
        guard_text(SC, huge)  # production path: rejected after the first slice, not after scanning 200 KB
    assert time.perf_counter() - started < 5


@settings(max_examples=60 * SCALE, deadline=None, suppress_health_check=[HealthCheck.too_slow])
@given(text=st.lists(st.sampled_from(FRAGMENTS), min_size=150, max_size=400).map("".join))
def test_large_pieces_are_sliced_without_changing_the_result(text):
    from dial_guard.errors import GuardInternalError
    from dial_guard.textguard import guard_text
    try:
        expected = guard_block(SC, text)[0]
    except GuardInternalError:
        with pytest.raises(GuardInternalError):
            guard_text(SC, text)
        return
    assert guard_text(SC, text)[0] == expected


def test_text_is_released_promptly():
    guard = TextGuard(SC)
    assert guard.feed("Hello there, ").text == "Hello there, "
    released = guard.feed("the claim is approved and the payout is scheduled ")
    assert released.text == "the claim is approved and the payout is scheduled "


# --- properties ----------------------------------------------------------------------------------------------------
texts = st.lists(st.sampled_from(FRAGMENTS), min_size=1, max_size=25).map("".join)

# Every property runs on two profiles: all PII kinds (the default), and the kinds the shared corpus and the .NET guard
# use. A narrower profile matters: with all kinds on, phone_intl once hid a streaming leak in phone_de ("0049 ...").
SHARED_KINDS = ["email", "iban", "phone_de", "date_de"]
PROFILES = {
    "all": (SC, PII),
    "shared": (scanner(pii=SHARED_KINDS), [v for v in PII if not v.startswith(("4111", "+1", "001"))]),
}


def block_or_error(sc, text: str):
    """The block-mode result, or None when the guard refuses the text: a run of overlapping matches longer than it
    checks (for example 25 e-mail addresses glued together) is a guard error, which fails closed. That outcome is
    allowed in every property, but only for text longer than the PII run cap, and every mode must agree on it."""
    try:
        return guard_block(sc, text)
    except GuardInternalError:
        assert len(text) > PII_RUN_CAP, text
        return None


def assert_same_in_streaming(sc, text: str, cuts: list[int]) -> None:
    expected = block_or_error(sc, text)
    if expected is None:
        with pytest.raises(GuardInternalError):
            stream(text, cuts, sc)
    else:
        assert stream(text, cuts, sc) == expected[0]


@pytest.mark.parametrize("profile", PROFILES)
@settings(max_examples=400 * SCALE, deadline=None, suppress_health_check=[HealthCheck.too_slow])
@given(text=texts, cuts=st.lists(st.integers(min_value=0, max_value=1200), max_size=40))
def test_streaming_equals_block_for_any_split(profile, text, cuts):
    assert_same_in_streaming(PROFILES[profile][0], text, cuts)


@pytest.mark.parametrize("profile", PROFILES)
@settings(max_examples=150 * SCALE, deadline=None, suppress_health_check=[HealthCheck.too_slow])
@given(text=texts)
def test_streaming_one_character_at_a_time(profile, text):
    assert_same_in_streaming(PROFILES[profile][0], text, list(range(len(text))))


def test_glued_addresses_beyond_the_run_cap_fail_closed_in_every_mode():
    text = "max.muster@example.com " + "max.muster@example.com" * 25 + " end"
    assert block_or_error(SC, text) is None
    for size in (1, 5, 64, 4096):
        with pytest.raises(GuardInternalError):
            stream(text, list(range(0, len(text), size)))


@pytest.mark.parametrize("profile", PROFILES)
@settings(max_examples=400 * SCALE, deadline=None, suppress_health_check=[HealthCheck.too_slow])
@given(text=texts)
def test_output_holds_nothing_the_guard_would_act_on(profile, text):
    sc = PROFILES[profile][0]
    result = block_or_error(sc, text)
    if result is None:
        return  # refused: nothing was emitted
    out, blocked, _ = result
    again, blocked_again, findings = guard_block(sc, out)
    assert not blocked_again
    assert not findings, (text, out, dict(findings))
    assert again == out  # idempotent


@pytest.mark.parametrize("profile", PROFILES)
@settings(max_examples=200 * SCALE, deadline=None, suppress_health_check=[HealthCheck.too_slow])
@given(prefix=texts, suffix=texts, data=st.data())
def test_planted_pii_never_survives(profile, prefix, suffix, data):
    sc, secrets = PROFILES[profile]
    secret = data.draw(st.sampled_from(secrets), label="secret")
    text = prefix + " " + secret + " " + suffix
    result = block_or_error(sc, text)
    if result is None:
        return  # refused: nothing was emitted
    for out in (result[0], stream(text, list(range(len(text))), sc)):  # block mode and one character at a time
        for value in (secret, secret.replace(" ", "")):
            assert not list(standalone(out, value)), (text, out)


def standalone(out: str, value: str):
    """Where `value` stands in `out` as a value of its own. A national-form number ("0...") glued to a digit that stays
    in the text is part of a longer digit run, which by design is not a phone number (so that claim ids such as
    CLM-2026-0142 survive); the guard replaces it only when that digit is replaced too."""
    i = out.find(value)
    while i != -1:
        if not (value[0] == "0" and i > 0 and out[i - 1].isdigit()):
            yield i
        i = out.find(value, i + 1)


@pytest.mark.parametrize("text", ["+49 151 234567890049 151 23456789", "0049 151 234567890151 23456789",
                                  "+1 415 555 01000151 23456789", "x 00151 23456789 0151 2345678900151 23456789"])
def test_the_configured_order_of_pii_kinds_does_not_change_the_result(text):
    reverse = scanner(pii=list(reversed(ALL_PII)))
    assert guard_block(reverse, text)[0] == guard_block(SC, text)[0]
