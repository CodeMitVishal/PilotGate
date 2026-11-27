"""Guard events: one JSON line per finding, plus one summary line per request.

Schema: `dial/schema/guard-event.v1.schema.json`. Events say where something was found and what the guard did, never
the matched value. Identifiers that come from the request are kept only when they look like identifiers; anything
else is replaced by a short hash, so a client cannot write free text (or personal data) into the event log.
"""

from __future__ import annotations

import hashlib
import json
import re
import sys
import threading
import uuid
from collections import Counter
from datetime import UTC, datetime
from typing import Protocol

SCHEMA = "dial-guard.event.v1"

_ID_CONVERSATION = re.compile(r"^[A-Za-z0-9._:-]{1,128}$")
_ID_DEPLOYMENT = re.compile(r"^[A-Za-z0-9._:@/-]{1,200}$")

KIND_ACTIONS = {
    "injection": {"quarantined", "flagged"},
    "pii": {"redacted", "flagged"},
    "link": {"removed", "flagged"},
    "attachment_url": {"replaced", "rejected", "flagged"},
    "canary": {"blocked", "flagged"},
}


def safe_identifier(value: str | None, pattern: re.Pattern[str] = _ID_CONVERSATION) -> str | None:
    if value is None or value == "":
        return None
    if pattern.match(value):
        return value
    return "sha256:" + hashlib.sha256(value.encode("utf-8", "surrogatepass")).hexdigest()[:16]


def kind_of(detector: str) -> str:
    if detector.startswith("pii."):
        return "pii"
    if detector.startswith("link."):
        return "link"
    if detector.startswith("injection."):
        return "injection"
    if detector.startswith("attachment."):
        return "attachment_url"
    if detector == "canary":
        return "canary"
    raise ValueError(f"unknown detector {detector!r}")


def _now() -> str:
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


class EventSink(Protocol):
    def emit(self, events: list[dict]) -> None: ...


class JsonLinesSink:
    """Writes events as JSON lines to stdout and/or an append-only file."""

    def __init__(self, stdout: bool = True, path: str | None = None) -> None:
        self._stdout = stdout
        self._lock = threading.Lock()
        self._file = open(path, "a", encoding="utf-8", buffering=1) if path else None  # noqa: SIM115

    def emit(self, events: list[dict]) -> None:
        lines = "".join(json.dumps(e, ensure_ascii=False, separators=(",", ":")) + "\n" for e in events)
        with self._lock:
            if self._stdout:
                sys.stdout.write(lines)
                sys.stdout.flush()
            if self._file is not None:
                self._file.write(lines)

    def close(self) -> None:
        if self._file is not None:
            self._file.close()


class MemorySink:
    def __init__(self) -> None:
        self.events: list[dict] = []

    def emit(self, events: list[dict]) -> None:
        self.events.extend(events)


class Recorder:
    """Collects what one request's guard did; turns it into events when the request ends."""

    def __init__(self, conversation_id: str | None, deployment: str | None, stream: bool) -> None:
        self.request_id = uuid.uuid4().hex
        self.conversation_id = safe_identifier(conversation_id)
        self.deployment = safe_identifier(deployment, _ID_DEPLOYMENT)
        self.stream = stream
        self.profile = "unknown"
        self.mode = "enforce"
        self.findings: Counter[tuple[str, str, str, str, str]] = Counter()
        self.errors: list[tuple[str, str]] = []
        self.passthroughs: Counter[str] = Counter()
        self.blocked = False
        self.rejected = False

    def configure(self, profile: str, mode: str) -> None:
        self.profile, self.mode = profile, mode

    def finding(self, direction: str, location: str, detector: str, action: str, count: int = 1) -> None:
        kind = kind_of(detector)
        if action not in KIND_ACTIONS[kind]:
            raise ValueError(f"action {action!r} not valid for {kind}")
        self.findings[(kind, direction, location, detector, action)] += count
        if action == "blocked":
            self.blocked = True
        if action == "rejected":
            self.rejected = True

    def guard_error(self, error_type: str, phase: str) -> None:
        self.errors.append((error_type[:100], phase))

    def fail_open(self, location: str) -> None:
        self.passthroughs[location] += 1

    def _base(self, event_kind: str) -> dict:
        return {"schema": SCHEMA, "time": _now(), "event_id": uuid.uuid4().hex, "request_id": self.request_id,
                "conversation_id": self.conversation_id, "deployment": self.deployment, "profile": self.profile,
                "mode": self.mode, "kind": event_kind}

    def events(self, outcome: str, duration_ms: float) -> list[dict]:
        out = []
        for (kind, direction, location, detector, action), count in sorted(self.findings.items()):
            out.append({**self._base(kind), "direction": direction, "location": location, "detector": detector,
                        "action": action, "count": count})
        for error_type, phase in self.errors:
            out.append({**self._base("guard_error"), "phase": phase, "error_type": error_type})
        for location, count in sorted(self.passthroughs.items()):
            out.append({**self._base("fail_open_passthrough"), "location": location, "action": "passed",
                        "count": count})
        totals: Counter[str] = Counter()
        for (kind, *_), count in self.findings.items():
            totals[kind] += count
        out.append({**self._base("request_completed"), "outcome": outcome, "stream": self.stream,
                    "duration_ms": round(max(duration_ms, 0.0), 3), "findings": dict(sorted(totals.items())),
                    "guard_errors": len(self.errors), "fail_open_passthroughs": sum(self.passthroughs.values())})
        return out
