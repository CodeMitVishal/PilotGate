"""Screenshots of the evidence pages with headless Microsoft Edge (a throwaway profile, never the user's), trimmed.
Usage: python shoot.py <evidence-dir> <out-dir> <section-id>... [--report <report.html>]
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image, ImageChops

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
D, OUT = Path(sys.argv[1]), Path(sys.argv[2])
args = sys.argv[3:]
report = None
if "--report" in args:
    i = args.index("--report")
    report, args = Path(args[i + 1]), args[:i] + args[i + 2:]
OUT.mkdir(parents=True, exist_ok=True)
profile = Path(tempfile.mkdtemp(prefix="edge-evidence-"))


def trim(png: Path) -> tuple[int, int]:
    image = Image.open(png).convert("RGB")
    box = ImageChops.difference(image, Image.new("RGB", image.size, (255, 255, 255))).getbbox()
    if box:
        image = image.crop((0, 0, image.width, min(image.height, box[3] + 28)))
    image.save(png, optimize=True)
    return image.size


def shoot(url: str, png: Path, width: int = 1280, height: int = 7000) -> None:
    subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                    "--disable-extensions", "--disable-sync", f"--user-data-dir={profile}",
                    f"--window-size={width},{height}", "--virtual-time-budget=5000", f"--screenshot={png}", url],
                   capture_output=True, timeout=180)
    if not png.exists():
        raise SystemExit(f"no screenshot for {url}")
    print(png.name, trim(png))


try:
    page = (D / "flow.html").resolve().as_uri()
    for n, section in enumerate(args, 1):
        shoot(f"{page}?only={section}", OUT / f"{n:02d}-{section}.png")
    if report:
        shoot(report.resolve().as_uri(), OUT / f"{len(args) + 1:02d}-pilotgate-report.png", height=2400)
finally:
    shutil.rmtree(profile, ignore_errors=True)
