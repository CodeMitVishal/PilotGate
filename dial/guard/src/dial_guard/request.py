"""Request side: quarantines untrusted text before the model sees it.

* tool and function messages, and messages with an unknown role: content quarantined;
* inline text attachments in any message, and stage content in assistant history: quarantined;
* user messages: flagged when they look like injection, never changed;
* system and developer messages: never touched;
* user attachments whose URL is neither DIAL storage nor an allowed host: request rejected (HTTP 400), as EPAM's
  `reject-external-links` example does. In history (assistant messages) such URLs are only flagged.

Messages are never mutated in place; changed messages are copies.
"""

from __future__ import annotations

import copy
from collections.abc import Callable

from .config import GuardConfig
from .errors import AttachmentRejected, GuardInternalError
from .links import LinkPolicy, classify_target
from .quarantine import PLACEHOLDER, quarantine, sanitize

_TEXT_TYPES = ("application/json", "application/xml", "application/x-yaml", "application/yaml")
UNTRUSTED_ROLES = ("tool", "function")
TRUSTED_ROLES = ("system", "developer")


def _is_text_attachment(item: dict) -> bool:
    kind = item.get("type")
    return kind is None or (isinstance(kind, str) and (kind.startswith("text/") or kind in _TEXT_TYPES))


class RequestGuard:
    """`report(location, detector, action, count)` records findings."""

    def __init__(self, config: GuardConfig, policy: LinkPolicy, report: Callable[[str, str, str, int], None]) -> None:
        self._config = config
        self._policy = policy
        self._enforce = config.mode == "enforce"
        self._report = report

    # --- text ---------------------------------------------------------------------------------------------------
    def _clean(self, text: str, where: str) -> str:
        """Quarantine untrusted text; returns the text to forward."""
        if not self._config.quarantine_untrusted:
            return text
        sanitized, hidden = sanitize(text)
        quarantined, instructions = quarantine(sanitized, self._config.tool_names)
        action = "quarantined" if self._enforce else "flagged"
        if hidden:
            self._report(where, "injection.hidden_text", action, hidden)
        if instructions:
            self._report(where, "injection.instruction", action, instructions)
        if not self._enforce or not (hidden or instructions):
            return text
        return quarantined.strip()

    def _flag(self, text: str, where: str) -> None:
        if not self._config.flag_user_messages:
            return
        sanitized, hidden = sanitize(text)
        _, instructions = quarantine(sanitized, self._config.tool_names)
        if hidden:
            self._report(where, "injection.hidden_text", "flagged", hidden)
        if instructions:
            self._report(where, "injection.instruction", "flagged", instructions)

    def _content(self, content: object, where: str, untrusted: bool) -> object:
        if isinstance(content, str):
            if untrusted:
                return self._clean(content, where)
            self._flag(content, where)
            return content
        if isinstance(content, list):
            out = []
            for i, part in enumerate(content):
                if isinstance(part, dict) and part.get("type") == "text" and isinstance(part.get("text"), str):
                    if untrusted:
                        cleaned = self._clean(part["text"], f"{where}[{i}]")
                        out.append(part if cleaned == part["text"] else {**part, "text": cleaned})
                    else:
                        self._flag(part["text"], f"{where}[{i}]")
                        out.append(part)
                else:
                    out.append(part)
            return out
        if content is None:
            return None
        raise GuardInternalError("message content is neither text nor a list of parts")

    # --- attachments and stages ---------------------------------------------------------------------------------
    def _attachments(self, items: object, where: str, reject: bool) -> object:
        if items is None:
            return None
        if not isinstance(items, list):
            raise GuardInternalError("attachments is not a list")
        out = []
        for j, item in enumerate(items):
            if not isinstance(item, dict):
                raise GuardInternalError("attachment is not an object")
            here = f"{where}.attachments[{j}]"
            entry = dict(item)
            url = item.get("url")
            if isinstance(url, str):
                refusal = classify_target(url, self._policy)
                if refusal is not None:
                    suffix = refusal.split(".", 1)[1]
                    if reject and self._config.reject_external_attachments and self._enforce:
                        self._report(f"{here}.url", "attachment." + suffix, "rejected", 1)
                        raise AttachmentRejected()
                    self._report(f"{here}.url", "attachment." + suffix, "flagged", 1)
            data = item.get("data")
            if isinstance(data, str) and _is_text_attachment(item):
                entry["data"] = self._clean(data, f"{here}.data")
            out.append(entry)
        return out

    def _stages(self, stages: object, where: str) -> object:
        if stages is None:
            return None
        if not isinstance(stages, list):
            raise GuardInternalError("stages is not a list")
        out = []
        for s, stage in enumerate(stages):
            if not isinstance(stage, dict):
                raise GuardInternalError("stage is not an object")
            here = f"{where}.stages[{s}]"
            entry = dict(stage)
            if isinstance(stage.get("content"), str):
                entry["content"] = self._clean(stage["content"], f"{here}.content")
            if stage.get("attachments") is not None:
                entry["attachments"] = self._attachments(stage["attachments"], here, reject=False)
            out.append(entry)
        return out

    # --- messages -----------------------------------------------------------------------------------------------
    def process_message(self, index: int, message: dict) -> dict:
        if not isinstance(message, dict):
            raise GuardInternalError("message is not an object")
        role = message.get("role")
        if role in TRUSTED_ROLES:
            return message
        where = f"messages[{index}]"
        out = dict(message)
        if role != "assistant":
            out["content"] = self._content(message.get("content"), f"{where}.content",
                                           untrusted=role != "user")
        cc = message.get("custom_content")
        if isinstance(cc, dict):
            new_cc = dict(cc)
            if cc.get("attachments") is not None:
                new_cc["attachments"] = self._attachments(cc["attachments"], f"{where}.custom_content",
                                                          reject=role == "user")
            if cc.get("stages") is not None:
                new_cc["stages"] = self._stages(cc["stages"], f"{where}.custom_content")
            out["custom_content"] = new_cc
        elif cc is not None:
            raise GuardInternalError("custom_content is not an object")
        if "content" not in message:
            out.pop("content", None)
        return out if self._enforce else copy.deepcopy(message)


__all__ = ["PLACEHOLDER", "RequestGuard"]
