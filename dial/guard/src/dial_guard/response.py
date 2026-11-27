"""Response side: guards every text field of every choice, chunk by chunk.

Guarded fields: `content`, `refusal`, `reasoning_content`, stage `name` and `content`, attachment `title` and `data`
(also inside stages). Each field has its own `TextGuard`, keyed by choice and path. Attachment URLs outside the policy
replace the attachment with a stub at the same index (DIAL merges lists by index, so indices must stay contiguous).
`tool_calls` and `function_call` are forwarded byte-identical; their arguments are scanned at the end of the choice
and findings are recorded only.

All strings in DIAL chunks are concatenated when merged (aidial_sdk `merge_chunks`), so a held-back fragment can be
released in any later chunk. A choice's buffers are flushed before the chunk that carries its `finish_reason`.
"""

from __future__ import annotations

import copy
from collections.abc import Callable

from .detectors import Scanner
from .errors import GuardInternalError
from .links import LinkPolicy, classify_target
from .textguard import TextGuard, guard_text

DELTA_TEXT_FIELDS = ("content", "refusal", "reasoning_content")
BLOCK_NOTICE = "\n\n[DIAL Guard stopped this response: it contained protected content.]"
ATTACHMENT_STUB = {"type": "text/markdown", "title": "Attachment removed by DIAL Guard",
                   "data": "This attachment pointed outside the allowed locations and was removed."}

Path = tuple  # ("content",) | ("stage", i, "name") | ("attachment", i, "data") | ("stage", i, "attachment", j, "data")


def location(choice: int, path: Path) -> str:
    out = f"choices[{choice}]"
    i = 0
    while i < len(path):
        part = path[i]
        if part == "stage":
            out += f".stages[{path[i + 1]}]"
            i += 2
        elif part == "attachment":
            out += f".attachments[{path[i + 1]}]"
            i += 2
        elif part == "tool_call":
            out += f".tool_calls[{path[i + 1]}]"
            i += 2
        else:
            out += f".{part}"
            i += 1
    return out


def _index(item: object, position: int) -> int:
    """Index of a list item. Streaming deltas carry an explicit index; the single chunk the SDK builds from a
    non-streaming response does not (spike fixture interceptor/block_toolcall.json), so the position is used then,
    as the SDK's own traversal does."""
    if not isinstance(item, dict):
        raise GuardInternalError("list item in a response chunk is not an object")
    if "index" not in item:
        return position
    idx = item["index"]
    if type(idx) is not int or idx < 0:
        raise GuardInternalError("list item in a response chunk has an invalid index")
    return idx


def _validate_text(value: object, name: str) -> None:
    if value is not None and not isinstance(value, str):
        raise GuardInternalError(f"{name} is not a string")


def _validate_attachments(items: object) -> None:
    if items is None:
        return
    if not isinstance(items, list):
        raise GuardInternalError("attachments is not a list")
    for position, item in enumerate(items):
        _index(item, position)
        for key in ("title", "data", "url", "reference_url"):
            _validate_text(item.get(key), key)


def _validate(chunk: object) -> None:
    """Structural check of a response chunk; raises GuardInternalError on anything the guard cannot interpret."""
    if not isinstance(chunk, dict):
        raise GuardInternalError("response chunk is not an object")
    choices = chunk.get("choices")
    if choices is None:
        return
    if not isinstance(choices, list):
        raise GuardInternalError("choices is not a list")
    for position, choice in enumerate(choices):
        _index(choice, position)
        delta = choice.get("delta")
        if delta is None:
            continue
        if not isinstance(delta, dict):
            raise GuardInternalError("delta is not an object")
        for key in DELTA_TEXT_FIELDS:
            _validate_text(delta.get(key), key)
        cc = delta.get("custom_content")
        if cc is not None:
            if not isinstance(cc, dict):
                raise GuardInternalError("custom_content is not an object")
            stages = cc.get("stages")
            if stages is not None:
                if not isinstance(stages, list):
                    raise GuardInternalError("stages is not a list")
                for position, stage in enumerate(stages):
                    _index(stage, position)
                    _validate_text(stage.get("name"), "name")
                    _validate_text(stage.get("content"), "content")
                    _validate_attachments(stage.get("attachments"))
            _validate_attachments(cc.get("attachments"))
        tool_calls = delta.get("tool_calls")
        if tool_calls is not None:
            if not isinstance(tool_calls, list):
                raise GuardInternalError("tool_calls is not a list")
            for position, call in enumerate(tool_calls):
                _index(call, position)


def _merge_text(delta: dict, path: Path, text: str) -> None:
    """Append `text` to the field at `path` inside a delta, creating indexed list entries as needed (a new entry is
    only ever created in streaming, where lists are indexed; a non-streaming chunk already holds every entry)."""
    if not text:
        return
    if len(path) == 1:
        delta[path[0]] = (delta.get(path[0]) or "") + text
        return
    container = delta.setdefault("custom_content", {})
    rest = path
    while True:
        kind, idx, rest = rest[0], rest[1], rest[2:]
        items = container.setdefault("stages" if kind == "stage" else "attachments", [])
        entry = next((e for pos, e in enumerate(items) if isinstance(e, dict) and e.get("index", pos) == idx), None)
        if entry is None:
            entry = {"index": idx}
            items.append(entry)
        if len(rest) == 1:
            entry[rest[0]] = (entry.get(rest[0]) or "") + text
            return
        container = entry


class _Choice:
    __slots__ = ("guards", "tool_args", "replaced", "blocked")

    def __init__(self) -> None:
        self.guards: dict[Path, TextGuard] = {}
        self.tool_args: dict[int, list[str]] = {}
        self.replaced: set[Path] = set()
        self.blocked = False


class ResponseGuard:
    """Turns upstream chunks into guarded chunks. `report(location, detector, action, count)` records findings."""

    def __init__(self, scanner: Scanner, policy: LinkPolicy, enforce: bool,
                 report: Callable[[str, str, str, int], None]) -> None:
        self._scanner = scanner
        self._policy = policy
        self._enforce = enforce
        self._report = report
        self._choices: dict[int, _Choice] = {}

    # --- public -------------------------------------------------------------------------------------------------
    def process(self, chunk: dict) -> list[dict]:
        """Chunks to send instead of `chunk` (none when everything in it is held back).

        The chunk's structure is validated before any buffer changes, so a malformed chunk never leaves half of
        itself inside the buffers."""
        _validate(chunk)
        choices = chunk.get("choices")
        if choices is None:
            return [chunk]
        new_choices = [c for c in (self._choice(ch, pos) for pos, ch in enumerate(choices)) if c is not None]
        rest = {k: v for k, v in chunk.items() if k != "choices"}
        meaningful = {k for k in rest if k not in ("id", "object", "created", "model", "system_fingerprint")}
        if not new_choices and choices and not meaningful:
            return []
        return [{**rest, "choices": new_choices}]

    def finish(self) -> list[dict]:
        """Release everything still held at the end of the stream (choices that never sent a finish_reason)."""
        out = []
        for idx in sorted(self._choices):
            st = self._choices[idx]
            if st.blocked:
                continue
            delta: dict = {}
            blocked = self._flush(idx, st, delta)
            self._scan_tools(idx, st)
            if blocked:
                out.append({"choices": [self._blocked_choice(idx, st, delta)]})
            elif delta:
                out.append({"choices": [{"index": idx, "delta": delta}]})
        return out

    def release_raw(self) -> list[dict]:
        """Fail-open: everything held back, unchecked. Returns chunks and reports nothing (the caller records it)."""
        out = []
        for idx in sorted(self._choices):
            st = self._choices[idx]
            if st.blocked:
                continue
            delta: dict = {}
            for path, guard in st.guards.items():
                _merge_text(delta, path, guard.take_raw())
            if delta:
                out.append({"choices": [{"index": idx, "delta": delta}]})
        return out

    def held_locations(self) -> list[str]:
        return [location(i, p) for i, st in sorted(self._choices.items()) for p, g in st.guards.items() if g.holding]

    # --- per choice ---------------------------------------------------------------------------------------------
    def _guard(self, st: _Choice, path: Path) -> TextGuard:
        g = st.guards.get(path)
        if g is None:
            g = st.guards[path] = TextGuard(self._scanner)
        return g

    def _record_guard(self, idx: int, path: Path, guard: TextGuard) -> None:
        for detector, count in guard.findings.items():
            action = "flagged" if not self._enforce else {"pii": "redacted", "link": "removed",
                                                          "canary": "blocked"}[detector.split(".", 1)[0]]
            self._report(location(idx, path), detector, action, count)
        guard.findings.clear()

    def _choice(self, choice: object, position: int) -> dict | None:
        idx = _index(choice, position)
        assert isinstance(choice, dict)
        st = self._choices.setdefault(idx, _Choice())
        if st.blocked:
            return None
        delta = choice.get("delta")
        new_delta: dict = {}
        blocked = False
        if delta is not None:
            if not isinstance(delta, dict):
                raise GuardInternalError("delta is not an object")
            for key, value in delta.items():
                if key in DELTA_TEXT_FIELDS and isinstance(value, str):
                    guard = self._guard(st, (key,))
                    emitted = guard.feed(value)
                    self._record_guard(idx, (key,), guard)
                    if emitted.text:
                        new_delta[key] = emitted.text
                    blocked = blocked or emitted.blocked
                elif key in DELTA_TEXT_FIELDS and value is not None:
                    raise GuardInternalError(f"{key} is not a string")
                elif key == "custom_content" and value is not None:
                    cc, cc_blocked = self._custom_content(idx, st, value)
                    if cc:
                        new_delta[key] = cc
                    blocked = blocked or cc_blocked
                elif key == "tool_calls" and value is not None:
                    self._collect_tool_calls(st, value)
                    new_delta[key] = value
                elif key == "function_call" and value is not None:
                    if isinstance(value, dict) and isinstance(value.get("arguments"), str):
                        st.tool_args.setdefault(-1, []).append(value["arguments"])
                    new_delta[key] = value
                else:
                    new_delta[key] = value
        finish_reason = choice.get("finish_reason")
        if not blocked and finish_reason:
            blocked = self._flush(idx, st, new_delta)
            self._scan_tools(idx, st)
        if blocked:
            return self._blocked_choice(idx, st, new_delta)
        rest = {k: v for k, v in choice.items() if k != "delta"}
        if not new_delta and not finish_reason and set(rest) <= {"index", "finish_reason", "logprobs"} and \
                rest.get("logprobs") is None:
            return None
        return {**rest, "delta": new_delta}

    def _flush(self, idx: int, st: _Choice, delta: dict) -> bool:
        blocked = False
        for path, guard in st.guards.items():
            emitted = guard.finish()
            self._record_guard(idx, path, guard)
            _merge_text(delta, path, emitted.text)
            blocked = blocked or emitted.blocked
            if blocked:
                break
        return blocked

    def _blocked_choice(self, idx: int, st: _Choice, delta: dict) -> dict:
        st.blocked = True
        for path, guard in st.guards.items():
            self._record_guard(idx, path, guard)
        delta.pop("tool_calls", None)
        delta.pop("function_call", None)
        delta["content"] = (delta.get("content") or "") + BLOCK_NOTICE
        return {"index": idx, "delta": delta, "finish_reason": "content_filter"}

    # --- custom content -----------------------------------------------------------------------------------------
    def _custom_content(self, idx: int, st: _Choice, cc: object) -> tuple[dict, bool]:
        if not isinstance(cc, dict):
            raise GuardInternalError("custom_content is not an object")
        out: dict = {}
        blocked = False
        for key, value in cc.items():
            if key == "stages" and value is not None:
                if not isinstance(value, list):
                    raise GuardInternalError("stages is not a list")
                stages = []
                for pos, stage in enumerate(value):
                    s, b = self._stage(idx, st, stage, pos)
                    stages.append(s)
                    blocked = blocked or b
                out[key] = stages
            elif key == "attachments" and value is not None:
                atts, b = self._attachments(idx, st, value, ())
                out[key] = atts
                blocked = blocked or b
            else:
                out[key] = value
        return out, blocked

    def _text_field(self, idx: int, st: _Choice, path: Path, value: object, target: dict, key: str) -> bool:
        if value is None:
            target[key] = None
            return False
        if not isinstance(value, str):
            raise GuardInternalError(f"{key} is not a string")
        guard = self._guard(st, path)
        emitted = guard.feed(value)
        self._record_guard(idx, path, guard)
        if emitted.text:
            target[key] = emitted.text
        return emitted.blocked

    def _stage(self, idx: int, st: _Choice, stage: object, position: int) -> tuple[dict, bool]:
        si = _index(stage, position)
        assert isinstance(stage, dict)
        out: dict = {}
        blocked = False
        for key, value in stage.items():
            if key in ("name", "content"):
                blocked = self._text_field(idx, st, ("stage", si, key), value, out, key) or blocked
            elif key == "attachments" and value is not None:
                atts, b = self._attachments(idx, st, value, ("stage", si))
                out[key] = atts
                blocked = blocked or b
            else:
                out[key] = value
        return out, blocked

    def _attachments(self, idx: int, st: _Choice, items: object, prefix: Path) -> tuple[list, bool]:
        if not isinstance(items, list):
            raise GuardInternalError("attachments is not a list")
        out = []
        blocked = False
        for pos, item in enumerate(items):
            ai = _index(item, pos)
            assert isinstance(item, dict)
            path = (*prefix, "attachment", ai)
            if path in st.replaced:
                continue
            refusal = None
            for key in ("url", "reference_url"):
                value = item.get(key)
                if isinstance(value, str) and refusal is None:
                    refusal = classify_target(value, self._policy)
            if refusal is not None:
                action = "replaced" if self._enforce else "flagged"
                self._report(location(idx, (*path, "url")), "attachment." + refusal.split(".", 1)[1], action, 1)
                if self._enforce:
                    st.replaced.add(path)
                    for field in ("title", "data"):  # anything held for this attachment is dropped with it
                        g = st.guards.pop((*path, field), None)
                        if g is not None:
                            self._record_guard(idx, (*path, field), g)
                    stub = copy.deepcopy(ATTACHMENT_STUB)
                    if "index" in item:  # keep the list's shape: indexed when streaming, positional otherwise
                        stub["index"] = ai
                    out.append(stub)
                    continue
            entry: dict = {}
            for key, value in item.items():
                if key in ("title", "data"):
                    blocked = self._text_field(idx, st, (*path, key), value, entry, key) or blocked
                else:
                    entry[key] = value
            out.append(entry)
        return out, blocked

    # --- tool calls ---------------------------------------------------------------------------------------------
    @staticmethod
    def _collect_tool_calls(st: _Choice, tool_calls: object) -> None:
        if not isinstance(tool_calls, list):
            raise GuardInternalError("tool_calls is not a list")
        for pos, call in enumerate(tool_calls):
            ti = _index(call, pos)
            assert isinstance(call, dict)
            fn = call.get("function")
            if isinstance(fn, dict) and isinstance(fn.get("arguments"), str):
                st.tool_args.setdefault(ti, []).append(fn["arguments"])

    def _scan_tools(self, idx: int, st: _Choice) -> None:
        for ti, parts in sorted(st.tool_args.items()):
            _, _, findings = guard_text(self._scanner, "".join(parts))
            path: Path = ("function_call", "arguments") if ti < 0 else ("tool_call", ti, "arguments")
            for detector, count in findings.items():
                self._report(location(idx, path), detector, "flagged", count)
        st.tool_args.clear()
