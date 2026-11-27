"""Event schema v1 and the recorder (acceptance E1, writer side)."""

import io
import json

import jsonschema
import pytest

from conftest import EVENT_SCHEMA, validate_events
from dial_guard.events import JsonLinesSink, Recorder, safe_identifier

VALIDATOR = jsonschema.Draft202012Validator(EVENT_SCHEMA)


def sample_events():
    rec = Recorder("conv-1", "claimsassist", True)
    rec.configure("strict", "enforce")
    rec.finding("request", "messages[3].content", "injection.instruction", "quarantined", 2)
    rec.finding("request", "messages[1].custom_content.attachments[0].url", "attachment.host_not_allowed", "rejected")
    rec.finding("response", "choices[0].content", "pii.email", "redacted", 3)
    rec.finding("response", "choices[0].stages[1].attachments[0].data", "link.too_long", "removed")
    rec.finding("response", "choices[0].tool_calls[2].arguments", "pii.iban", "flagged")
    rec.finding("response", "choices[0].content", "canary", "blocked")
    rec.guard_error("RuntimeError", "response")
    rec.fail_open("choices[0].content")
    return rec.events("blocked", 12.5)


def test_recorder_events_match_the_schema():
    events = sample_events()
    validate_events(events)
    done = events[-1]
    assert done["kind"] == "request_completed"
    assert done["findings"] == {"attachment_url": 1, "canary": 1, "injection": 2, "link": 1, "pii": 4}
    assert (done["guard_errors"], done["fail_open_passthroughs"], done["outcome"]) == (1, 1, "blocked")
    assert len({e["event_id"] for e in events}) == len(events)
    assert len({e["request_id"] for e in events}) == 1


@pytest.mark.parametrize("mutation", [
    lambda e: e.update(value="max@example.com"),  # no free-text field can be added
    lambda e: e.update(location="choices[0].content max@example.com"),
    lambda e: e.update(detector="pii.max@example.com"),
    lambda e: e.update(conversation_id="max@example.com"),
    lambda e: e.update(action="deleted"),
    lambda e: e.update(kind="pii", detector="link.host_not_allowed"),
    lambda e: e.update(count=0),
    lambda e: e.update(schema="dial-guard.event.v2"),
    lambda e: e.pop("request_id"),
])
def test_schema_rejects_anything_that_could_carry_a_value(mutation):
    event = next(e for e in sample_events() if e["kind"] == "pii")
    mutation(event)
    assert list(VALIDATOR.iter_errors(event))


@pytest.mark.parametrize(("value", "expected"), [
    ("conv-123", "conv-123"), ("7b1c2c8e-1d2f-4c3a-9a7e-0f1e2d3c4b5a", "7b1c2c8e-1d2f-4c3a-9a7e-0f1e2d3c4b5a"),
    (None, None), ("", None)])
def test_identifiers_kept_when_they_look_like_identifiers(value, expected):
    assert safe_identifier(value) == expected


@pytest.mark.parametrize("value", ["max@example.com", "a b", "x" * 129, "line\nbreak", "\ud800"])
def test_other_identifiers_are_hashed(value):
    out = safe_identifier(value)
    assert out.startswith("sha256:") and len(out) == 23


def test_recorder_rejects_unknown_detectors_and_actions():
    rec = Recorder(None, None, False)
    with pytest.raises(ValueError):
        rec.finding("response", "choices[0].content", "pii.email", "removed")
    with pytest.raises(ValueError):
        rec.finding("response", "choices[0].content", "secret.thing", "flagged")


def test_json_lines_sink_writes_one_line_per_event(tmp_path, monkeypatch):
    path = tmp_path / "events.jsonl"
    out = io.StringIO()
    monkeypatch.setattr("sys.stdout", out)
    sink = JsonLinesSink(stdout=True, path=str(path))
    sink.emit(sample_events())
    sink.close()
    lines = path.read_text(encoding="utf-8").splitlines()
    assert len(lines) == len(sample_events()) and out.getvalue().count("\n") == len(lines)
    validate_events([json.loads(line) for line in lines])


def test_every_detector_the_code_can_emit_is_in_the_schema():
    from dial_guard.detectors import _PII  # noqa: PLC0415
    enum = set(EVENT_SCHEMA["properties"]["detector"]["enum"])
    emitted = {"pii." + k for k in _PII} | {"injection.instruction", "injection.hidden_text", "canary",
                                            "link.too_long", "link.contains_pii"}
    for refusal in ("host_not_allowed", "ip_address", "unparseable", "obfuscated", "unsafe_scheme"):
        emitted |= {"link." + refusal, "attachment." + refusal}
    assert emitted <= enum
