"""Link policy break-it matrix (acceptance G5)."""

import pytest

from dial_guard.links import LinkPolicy, classify_target

POLICY = LinkPolicy(("intranet.contoso.example",), "http://core:8080")

ALLOWED = [
    "https://intranet.contoso.example/kb",
    "https://docs.intranet.contoso.example/x",
    "HTTPS://INTRANET.CONTOSO.EXAMPLE/KB",
    "https://intranet.contoso.example:8443/kb",
    "http://intranet.contoso.example",
    "www.intranet.contoso.example/kb",
    "hxxps://intranet.contoso.example/kb",  # defanged, but the host is allowed
    "https://ｉｎｔｒａｎｅｔ.contoso.example/",  # full-width: browsers map it to the same host
    "files/bucket123/report.pdf",  # relative: DIAL storage
    "/v1/files/abc",
    "../relative/page",
    "#section",
    "ht!tp://x",  # "!" cannot appear in a scheme, so a browser resolves this as a relative path on the same origin
    "http://core:8080/v1/files/bucket/doc.pdf",  # under the DIAL Core URL
    " https://intranet.contoso.example/kb ",
]

REFUSED = [
    ("https://evil.example/upload", "link.host_not_allowed"),
    ("https://intranet.contoso.example.evil.example/login", "link.host_not_allowed"),
    ("https://intranet.contoso.example@evil.example/login", "link.host_not_allowed"),
    ("https://intranet.contoso.example:443@evil.example/", "link.host_not_allowed"),
    ("https://evil.example\\@intranet.contoso.example/", "link.host_not_allowed"),  # browsers read \ as /
    ("https://evil.example#@intranet.contoso.example", "link.host_not_allowed"),
    ("https://evil.example?@intranet.contoso.example", "link.host_not_allowed"),
    ("https://іntranet.contoso.example/login", "link.host_not_allowed"),  # Cyrillic i
    ("https://xn--ntranet-3ya.contoso.example/login", "link.host_not_allowed"),  # valid punycode of a homoglyph
    ("https://xn--ntranet-ufg.contoso.example/login", "link.unparseable"),  # invalid A-label
    ("https://intranet.contoso.example./kb", "link.host_not_allowed"),  # trailing dot: refused, strict
    ("https://contoso.example/", "link.host_not_allowed"),  # parent domain is not allowed
    ("https://otherintranet.contoso.example/", "link.host_not_allowed"),  # suffix without a dot
    ("http://192.0.2.10/collect", "link.ip_address"),
    ("http://0xC000020A/collect", "link.ip_address"),
    ("http://3221225994/collect", "link.ip_address"),
    ("http://0300.0000.0002.0012/", "link.ip_address"),
    ("http://[::1]/", "link.ip_address"),
    ("http://[2001:db8::1]:8080/x", "link.ip_address"),
    ("//evil.example/x", "link.host_not_allowed"),
    ("\\\\evil.example/x", "link.host_not_allowed"),
    ("hxxp://evil.example/payload", "link.host_not_allowed"),
    ("www.evil.example/login", "link.host_not_allowed"),
    ("javascript:alert(1)", "link.unsafe_scheme"),
    ("JaVaScRiPt:alert(1)", "link.unsafe_scheme"),
    ("java\tscript:alert(1)", "link.unsafe_scheme"),  # browsers drop the tab
    ("java\nscript:alert(1)", "link.unsafe_scheme"),
    ("data:text/html;base64,PHNjcmlwdD4=", "link.unsafe_scheme"),
    ("vbscript:msgbox(1)", "link.unsafe_scheme"),
    ("mailto:someone@evil.example", "link.unsafe_scheme"),
    ("file:///etc/passwd", "link.unsafe_scheme"),
    ("https://evi​l.example/", "link.obfuscated"),
    ("https://intra​net.contoso.example/", "link.obfuscated"),  # invisible character inside an allowed host
    ("https://%69ntranet.contoso.example/", "link.unparseable"),  # percent-encoded host: refused, strict
    ("https:///path-only", "link.unparseable"),
    ("https://", "link.unparseable"),
    ("https://evil.example .intranet.contoso.example/", "link.unparseable"),  # space inside: invalid, not repaired
    ("https://intranet.contoso.example @evil.example/", "link.unparseable"),
    ("http://core:8080.evil.example/x", "link.host_not_allowed"),  # looks like the DIAL URL, is not
    ("http://core:8080@evil.example/x", "link.host_not_allowed"),
    ("", "link.unparseable"),
]


@pytest.mark.parametrize("url", ALLOWED)
def test_allowed(url):
    assert classify_target(url, POLICY) is None


@pytest.mark.parametrize(("url", "detector"), REFUSED)
def test_refused(url, detector):
    assert classify_target(url, POLICY) == detector


def test_empty_allow_list_refuses_every_absolute_link():
    strict = LinkPolicy(())
    assert classify_target("https://intranet.contoso.example/kb", strict) == "link.host_not_allowed"
    assert classify_target("files/bucket/doc.pdf", strict) is None


def test_overlong_and_odd_input_never_raises():
    for url in ["https://" + "a" * 70 + ".example/", "https://" + "a." * 200 + "example/", "http://" + "\U0001f600" * 50,
                "https://ex ample.com/", "https://[bad", "\x00https://evil.example"]:
        assert classify_target(url, POLICY) is not None
