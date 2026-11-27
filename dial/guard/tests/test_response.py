"""Response guard over chunk sequences (acceptance G2, G3, G4)."""

import copy
import json
import random

import pytest

from conftest import ALLOWED, CANARY, scanner
from dial_guard.errors import GuardInternalError
from dial_guard.links import LinkPolicy
from dial_guard.response import ATTACHMENT_STUB, BLOCK_NOTICE, ResponseGuard


def make(enforce=True):
    reports = []
    guard = ResponseGuard(scanner(), LinkPolicy(tuple(ALLOWED), "http://core:8080"), enforce,
                          lambda loc, det, act, n: reports.append((loc, det, act, n)))
    return guard, reports


def chunk(delta, index=0, finish=None):
    return {"id": "x", "object": "chat.completion.chunk", "created": 1, "model": "m",
            "choices": [{"index": index, "delta": delta, "finish_reason": finish}]}


def run(guard, chunks):
    out = []
    for c in chunks:
        out.extend(guard.process(c))
    out.extend(guard.finish())
    return out


def merged(out, index=0):
    """Merge chunks the way DIAL's merge_chunks does (strings concatenate, lists merge by index)."""
    from aidial_sdk.utils.merge_chunks import merge
    acc = {}
    for c in copy.deepcopy(out):
        acc = merge(acc, {"choices": c.get("choices", [])})
    return next((c for c in acc.get("choices", []) if c.get("index") == index), {})


def content_chunks(text, sizes):
    pieces, i = [], 0
    for n in sizes:
        pieces.append(text[i:i + n])
        i += n
    if i < len(text):
        pieces.append(text[i:])
    return [chunk({"role": "assistant"})] + [chunk({"content": p}) for p in pieces if p] + [chunk({}, finish="stop")]


def test_content_is_guarded_and_flushed_before_finish():
    guard, reports = make()
    text = "Write to max.muster@example.com or see https://evil.example/x now."
    out = run(guard, content_chunks(text, [3, 7, 11, 2, 9]))
    choice = merged(out)
    assert choice["delta"]["content"] == "Write to [EMAIL] or see [link removed] now."
    finish_positions = [i for i, c in enumerate(out) for ch in c["choices"] if ch.get("finish_reason")]
    content_positions = [i for i, c in enumerate(out) for ch in c["choices"] if (ch.get("delta") or {}).get("content")]
    assert max(content_positions) <= min(finish_positions)
    assert sum(1 for c in out for ch in c["choices"] if ch.get("finish_reason")) == 1  # finish_reason never duplicated
    assert ("choices[0].content", "pii.email", "redacted", 1) in reports
    assert ("choices[0].content", "link.host_not_allowed", "removed", 1) in reports


@pytest.mark.parametrize("seed", range(25))
def test_any_chunking_gives_the_same_final_content(seed):
    rng = random.Random(seed)
    text = ("Call +49 151 23456789, IBAN DE89 3704 0044 0532 0130 00, [docs](https://intranet.contoso.example/a), "
            "[x](https://evil.example/y) and ![p](https://evil.example/t.gif) done.")
    sizes = [rng.randint(1, 9) for _ in range(len(text))]
    guard, _ = make()
    assert merged(run(guard, content_chunks(text, sizes)))["delta"]["content"] == (
        "Call [PHONE], IBAN [IBAN ending 3000], [docs](https://intranet.contoso.example/a), x and [image removed] done.")


def test_tool_calls_pass_byte_identical_and_are_scanned():
    guard, reports = make()
    calls = [chunk({"tool_calls": [{"index": 0, "id": "call_1", "type": "function",
                                    "function": {"name": "lookup", "arguments": ""}}]}),
             chunk({"tool_calls": [{"index": 0, "function": {"arguments": "{\"email\":\"max@exam"}}]}),
             chunk({"tool_calls": [{"index": 0, "function": {"arguments": "ple.com\"}"}}]}),
             chunk({}, finish="tool_calls")]
    original = copy.deepcopy(calls)
    out = run(guard, calls)
    sent = [ch["delta"]["tool_calls"] for c in out for ch in c["choices"] if "tool_calls" in ch["delta"]]
    assert json.dumps(sent) == json.dumps([c["choices"][0]["delta"]["tool_calls"] for c in original[:3]])
    assert ("choices[0].tool_calls[0].arguments", "pii.email", "flagged", 1) in reports


def test_attachment_with_refused_url_becomes_a_stub_at_the_same_index():
    guard, reports = make()
    out = run(guard, [chunk({"custom_content": {"attachments": [
        {"index": 0, "type": "text/markdown", "title": "Notes", "data": "Mail max@example.com"},
        {"index": 1, "type": "text/html", "title": "External", "url": "https://evil.example/doc"},
        {"index": 2, "type": "application/pdf", "title": "Stored", "url": "files/bucket/doc.pdf"}]}}),
        chunk({"custom_content": {"attachments": [{"index": 1, "data": "late data for the removed one"}]}}),
        chunk({}, finish="stop")])
    attachments = merged(out)["delta"]["custom_content"]["attachments"]
    assert [a["index"] for a in attachments] == [0, 1, 2]
    assert attachments[0]["data"] == "Mail [EMAIL]"
    assert {k: v for k, v in attachments[1].items() if k != "index"} == ATTACHMENT_STUB
    assert attachments[2]["url"] == "files/bucket/doc.pdf"
    assert ("choices[0].attachments[1].url", "attachment.host_not_allowed", "replaced", 1) in reports


def test_stage_name_and_content_are_guarded():
    guard, _ = make()
    out = run(guard, [
        chunk({"custom_content": {"stages": [{"index": 0, "name": "Search max@example.com", "status": None}]}}),
        chunk({"custom_content": {"stages": [{"index": 0, "content": "Found https://evil.example/a in ",
                                              "status": None}]}}),
        chunk({"custom_content": {"stages": [{"index": 0, "content": "the register.", "status": "completed"}]}}),
        chunk({"content": "Done."}), chunk({}, finish="stop")])
    stage = merged(out)["delta"]["custom_content"]["stages"][0]
    assert stage["name"] == "Search [EMAIL]"
    assert stage["content"] == "Found [link removed] in the register."
    assert stage["status"] == "completed"


def test_canary_stops_the_choice_with_content_filter():
    guard, reports = make()
    out = run(guard, [chunk({"role": "assistant"}), chunk({"content": "The marker is SYS-CAN"}),
                      chunk({"content": "ARY-9921 and more"}), chunk({"content": " text"}),
                      chunk({"tool_calls": [{"index": 0, "id": "c", "type": "function",
                                             "function": {"name": "f", "arguments": "{}"}}]}),
                      chunk({}, finish="stop")])
    choice = merged(out)
    assert choice["delta"]["content"] == "The marker is " + BLOCK_NOTICE
    assert choice["finish_reason"] == "content_filter"
    assert "tool_calls" not in choice["delta"]
    assert CANARY not in json.dumps(out)
    assert ("choices[0].content", "canary", "blocked", 1) in reports


def test_two_choices_are_guarded_independently():
    guard, _ = make()
    out = run(guard, [chunk({"content": "a@b.co"}, 0), chunk({"content": "plain"}, 1),
                      chunk({"content": "m and more"}, 0), chunk({}, 1, "stop"), chunk({}, 0, "stop")])
    assert merged(out, 0)["delta"]["content"] == "[EMAIL] and more"
    assert merged(out, 1)["delta"]["content"] == "plain"


def test_usage_and_empty_choice_chunks_pass():
    guard, _ = make()
    usage = {"id": "x", "choices": [], "usage": {"prompt_tokens": 1, "completion_tokens": 2, "total_tokens": 3}}
    assert guard.process(copy.deepcopy(usage)) == [usage]
    assert guard.process({"usage": {"total_tokens": 3}}) == [{"usage": {"total_tokens": 3}}]


def test_monitor_mode_reports_but_does_not_replace_attachments():
    guard, reports = make(enforce=False)
    run(guard, [chunk({"content": "max@example.com"}), chunk({"custom_content": {"attachments": [
        {"index": 0, "url": "https://evil.example/x"}]}}), chunk({}, finish="stop")])
    assert ("choices[0].content", "pii.email", "flagged", 1) in reports
    assert ("choices[0].attachments[0].url", "attachment.host_not_allowed", "flagged", 1) in reports


def test_choice_without_finish_reason_is_flushed_at_the_end():
    guard, _ = make()
    out = run(guard, [chunk({"content": "ends with max@example.com"})])
    assert merged(out)["delta"]["content"] == "ends with [EMAIL]"


def test_non_streaming_chunk_with_positional_lists():
    """The SDK's chunk for a non-streaming response has no list indices (fixture interceptor/block_toolcall.json)."""
    guard, _ = make()
    block = {"choices": [{"index": 0, "finish_reason": "stop", "delta": {
        "role": "assistant", "content": "Mail max@example.com",
        "tool_calls": [{"id": "c1", "type": "function", "function": {"name": "f", "arguments": "{\"a\":1}"}}],
        "custom_content": {
            "attachments": [{"type": "text/markdown", "title": "t", "data": "IBAN DE89370400440532013000"},
                            {"type": "text/html", "url": "https://evil.example/x"}],
            "stages": [{"name": "s", "status": "completed", "content": "see https://evil.example/y"}]}}}]}
    [out] = guard.process(copy.deepcopy(block))
    delta = out["choices"][0]["delta"]
    assert delta["content"] == "Mail [EMAIL]"
    assert delta["tool_calls"] == block["choices"][0]["delta"]["tool_calls"]
    atts = delta["custom_content"]["attachments"]
    assert all("index" not in a for a in atts)  # the list keeps its positional shape
    assert atts[0]["data"] == "IBAN [IBAN ending 3000]" and atts[1] == ATTACHMENT_STUB
    assert delta["custom_content"]["stages"][0] == {"name": "s", "status": "completed",
                                                    "content": "see [link removed]"}


def test_fail_open_release_returns_held_text_raw():
    guard, _ = make()
    guard.process(chunk({"content": "held max@exa"}))
    released = guard.release_raw()
    assert "max@exa" in json.dumps(released)


@pytest.mark.parametrize("bad", [
    "not a dict", {"choices": "x"}, {"choices": ["x"]}, {"choices": [{"index": "0", "delta": {}}]},
    {"choices": [{"index": True, "delta": {}}]}, {"choices": [{"index": -1, "delta": {}}]},
    {"choices": [{"index": 0, "delta": "x"}]}, {"choices": [{"index": 0, "delta": {"content": 5}}]},
    {"choices": [{"index": 0, "delta": {"custom_content": []}}]},
    {"choices": [{"index": 0, "delta": {"custom_content": {"stages": [1]}}}]},
    {"choices": [{"index": 0, "delta": {"custom_content": {"stages": [{"index": "a", "name": "x"}]}}}]},
    {"choices": [{"index": 0, "delta": {"custom_content": {"attachments": [{"index": 0, "data": 1}]}}}]},
    {"choices": [{"index": 0, "delta": {"tool_calls": ["x"]}}]},
])
def test_malformed_chunks_raise_before_any_buffer_changes(bad):
    guard, _ = make()
    guard.process(chunk({"content": "kept "}))
    with pytest.raises(GuardInternalError):
        guard.process(bad)
    assert guard.held_locations() == []
