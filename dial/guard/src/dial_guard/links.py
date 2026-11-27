"""Link policy: may this URL appear in an answer or an attachment?

A URL is allowed when it is relative (stays on the DIAL origin), lies under the DIAL Core URL, or points to an allowed
host or one of its subdomains. Everything else is refused, including URLs a browser would still open:

* look-alike suffixes (`intranet.contoso.example.evil.example`) and userinfo tricks (`allowed@evil`),
* homoglyph hosts (compared IDNA-encoded, so Cyrillic `і` never equals Latin `i`) and punycode,
* raw IP addresses in any notation (dotted, decimal, octal, hex, IPv6),
* backslashes (browsers treat them as `/`) and protocol-relative `//host` hiding another host,
* invisible characters inside the URL, unsafe schemes (`javascript:`, `data:`, `mailto:` ...), unparseable URLs.

Defanged `hxxp://` links are judged by their host like any other link.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from urllib.parse import urljoin, urlsplit

import idna

from .normalize import INVISIBLE

_SCHEME = re.compile(r"^([A-Za-z][A-Za-z0-9+.-]*):")
_IPV4ISH = re.compile(r"^(?:0x[0-9a-f]+|\d+)(?:\.(?:0x[0-9a-f]+|\d+)){0,3}$", re.I)
_CONTROL = re.compile(r"[\x00-\x20\x7f]")
_EDGES = re.compile(r"^[\x00-\x20]+|[\x00-\x20]+$")


@dataclass(frozen=True, slots=True)
class LinkPolicy:
    allowed_hosts: tuple[str, ...]
    dial_url: str | None = None

    def host_allowed(self, ascii_host: str) -> bool:
        return any(ascii_host == a or ascii_host.endswith("." + a) for a in self.allowed_hosts)


def _ascii_host(url: str) -> tuple[str | None, str | None]:
    """Returns (ascii host, None) or (None, detector name of the refusal)."""
    try:
        host = urlsplit(url).hostname
    except ValueError:
        return None, "link.unparseable"
    if not host:
        return None, "link.unparseable"
    if ":" in host or _IPV4ISH.match(host):
        return None, "link.ip_address"
    try:
        return idna.encode(host, uts46=True).decode("ascii").lower(), None
    except (idna.IDNAError, UnicodeError, ValueError):
        return None, "link.unparseable"


def check_absolute(url: str, policy: LinkPolicy) -> str | None:
    """Policy check for an absolute http(s) URL. None means allowed, otherwise the refusal's detector name."""
    try:
        if policy.dial_url and urljoin(policy.dial_url + "/", url).startswith(policy.dial_url + "/"):
            return None
    except ValueError:
        return "link.unparseable"
    host, refusal = _ascii_host(url)
    if refusal:
        return refusal
    assert host is not None
    return None if policy.host_allowed(host) else "link.host_not_allowed"


def classify_target(target: str, policy: LinkPolicy) -> str | None:
    """Policy check for any link target (markdown link, image, attachment URL, bare URL).

    None means allowed, otherwise the detector name that refused it."""
    if any(c in INVISIBLE for c in target):
        return "link.obfuscated"
    # Like a browser: trim surrounding control characters and spaces, drop tab and newline anywhere. Any other space
    # or control character left inside makes the URL invalid, so it is refused rather than repaired.
    t = _EDGES.sub("", target).replace("\t", "").replace("\n", "").replace("\r", "")
    if not t or _CONTROL.search(t):
        return "link.unparseable"
    t = t.replace("\\", "/")
    if t.startswith("//"):
        return check_absolute("http:" + t, policy)
    scheme = _SCHEME.match(t)
    if scheme:
        name = scheme.group(1).lower()
        if name in ("http", "https"):
            return check_absolute(t, policy)
        if name in ("hxxp", "hxxps"):  # defanged: judged by its host like any other link
            return check_absolute("http" + t[4:], policy)
        return "link.unsafe_scheme"
    if t.lower().startswith("www."):
        return check_absolute("http://" + t, policy)
    return None  # relative: stays on the DIAL origin
