"""Errors the guard raises.

The interceptors SDK turns any exception into an HTTP error whose message is `str(exception)` (spike fact 15), which
would show internal details to every DIAL user. So the guard never lets a raw exception out: its own failures become
`GuardError` (a generic message with a reference id), and the app wrapper sanitizes the SDK's internal failures the
same way.
"""

from __future__ import annotations

from aidial_sdk.exceptions import HTTPException as DialException
from aidial_sdk.exceptions import InvalidRequestError


class GuardInternalError(Exception):
    """An invariant broke or input could not be understood. Triggers the configured failure mode."""


class GuardError(DialException):
    """Sanitized error returned to the client when the guard cannot check content (fail closed)."""

    def __init__(self, reference: str) -> None:
        message = f"DIAL Guard could not check this response. Reference: {reference}"
        super().__init__(message=message, status_code=500, type="internal_server_error", code="guard_error",
                         display_message=message)


class AttachmentRejected(InvalidRequestError):
    """A request attachment points outside DIAL storage and the allowed hosts."""

    def __init__(self) -> None:
        message = "DIAL Guard: attachments must point to DIAL storage or an allowed host."
        super().__init__(message=message, code="attachment_rejected", display_message=message)
