"""Configuration of DIAL Guard.

Two sources, by design:

* `GuardConfig` is the per-deployment policy. DIAL Core sends it in `custom_fields.interceptor_configuration`, taken
  from the `defaults` of the interceptor entry. Core replaces or removes any value a client sends (spike fact 6), so a
  client cannot loosen it. Every default is the strictest value, because an interceptor entry without `defaults` gets
  exactly these defaults. Unknown keys are rejected, so a typo in the DIAL config fails loudly instead of silently.
* `Settings` is the service configuration from environment variables (where to send events, the DIAL Core URL).
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Literal

import idna
from pydantic import BaseModel, ConfigDict, Field, field_validator

PiiKind = Literal["email", "iban", "phone_de", "date_de", "card", "phone_intl"]
ALL_PII: list[PiiKind] = ["email", "iban", "phone_de", "date_de", "card", "phone_intl"]


class GuardConfig(BaseModel):
    """Per-deployment policy (interceptor configuration)."""

    model_config = ConfigDict(extra="forbid", frozen=True)

    profile: str = Field(default="default", pattern=r"^[a-z0-9][a-z0-9-]{0,63}$",
                         description="Name of this policy profile; written to every event.")
    mode: Literal["enforce", "monitor"] = Field(
        default="enforce", description="enforce changes content; monitor only records what it would change.")
    on_guard_error: Literal["fail_closed", "fail_open"] = Field(
        default="fail_closed",
        description="fail_closed ends the response with an error if the guard itself fails; fail_open passes the "
                    "remaining content through unchecked and records it.")
    allowed_link_hosts: list[str] = Field(
        default_factory=list, max_length=200,
        description="Hosts (and their subdomains) that links and attachment URLs may point to.")
    pii: list[PiiKind] = Field(default_factory=lambda: list(ALL_PII), description="PII kinds to redact.")
    canaries: list[str] = Field(
        default_factory=list, max_length=50,
        description="Leak markers. A response that contains one is stopped at that point.")
    tool_names: list[str] = Field(
        default_factory=list, max_length=100,
        description="Tool names of the application; untrusted text that tells the model to call one is quarantined.")
    quarantine_untrusted: bool = Field(
        default=True, description="Quarantine instructions in tool messages, attachments and history stages.")
    flag_user_messages: bool = Field(default=True, description="Record user messages that look like injection.")
    reject_external_attachments: bool = Field(
        default=True, description="Reject request attachments that point outside DIAL storage and the allowed hosts.")
    max_link_length: int = Field(default=2048, ge=64, le=8192,
                                 description="Links longer than this are removed in any case.")
    max_scan_seconds: float = Field(
        default=5.0, ge=0.5, le=60.0,
        description="CPU time the guard may spend on one request; beyond it the failure mode applies.")

    @field_validator("allowed_link_hosts")
    @classmethod
    def _hosts(cls, hosts: list[str]) -> list[str]:
        out = []
        for host in hosts:
            h = host.strip().lower()
            if not h or "/" in h or ":" in h or "@" in h or h.startswith(".") or h.endswith("."):
                raise ValueError(f"not a host name: {host!r}")
            try:
                out.append(idna.encode(h, uts46=True).decode("ascii"))
            except idna.IDNAError as e:
                raise ValueError(f"not a valid host name: {host!r}") from e
        return out

    @field_validator("canaries")
    @classmethod
    def _canaries(cls, canaries: list[str]) -> list[str]:
        for c in canaries:
            if not 4 <= len(c) <= 200 or c != c.strip():
                raise ValueError("canaries must be 4 to 200 characters without surrounding spaces")
        return canaries

    @field_validator("tool_names")
    @classmethod
    def _tools(cls, names: list[str]) -> list[str]:
        for n in names:
            if not 1 <= len(n) <= 64 or not all(ch.isalnum() or ch in "_-." for ch in n):
                raise ValueError(f"not a tool name: {n!r}")
        return names


@dataclass(frozen=True, slots=True)
class Settings:
    """Service settings from the environment."""

    dial_url: str
    events_stdout: bool
    events_file: str | None

    @staticmethod
    def from_env(env: dict[str, str] | None = None) -> "Settings":
        e = os.environ if env is None else env
        dial_url = (e.get("DIAL_URL") or "").strip().rstrip("/")
        if not dial_url.startswith(("http://", "https://")):
            raise ValueError("DIAL_URL must be set to the DIAL Core base URL, for example http://core:8080")
        return Settings(
            dial_url=dial_url,
            events_stdout=(e.get("DIAL_GUARD_EVENTS_STDOUT", "true").strip().lower() not in ("0", "false", "no")),
            events_file=(e.get("DIAL_GUARD_EVENTS_FILE") or "").strip() or None,
        )
