# Renders every diagram of a built site on its own, to look at it: desktop and phone width, light and dark, in headless
# Microsoft Edge (no window, its own temporary profile, one Edge at a time).
#
#   python website/render_diagrams.py <built site> <out folder> [--pages games/,flows/export.html] [--views desktop,phone]
#                                     [--themes light,dark]
#
# A "diagram" is a Mermaid figure, a :::flow or a :::steps block. Files: <page>-<n>-<desktop|phone>-<light|dark>.png,
# cut to the diagram. The site's own CSS and site.js draw them, so the renders show what a reader sees.
from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

BLOCK = re.compile(r'<figure class="diagram-fig">.*?</figure>|<div class="flow" .*?</div>\n|<div class="steps">.*?</ol>\s*</div>', re.S)


def edge() -> str:
    for p in (Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"), Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe")):
        if p.exists():
            return str(p)
    raise SystemExit("Microsoft Edge not found")


def crop(png: Path):
    from PIL import Image, ImageChops
    im = Image.open(png).convert("RGB")
    bg = Image.new("RGB", im.size, im.getpixel((im.width - 2, im.height - 2)))
    box = ImageChops.difference(im, bg).getbbox()
    if box:
        l, t, r, b = box
        im.crop((max(0, l - 8), max(0, t - 8), min(im.width, r + 8), min(im.height, b + 8))).save(png)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("site")
    ap.add_argument("out")
    ap.add_argument("--pages", default="", help="comma-separated page paths or folder prefixes (default: every page)")
    ap.add_argument("--views", default="desktop,phone")
    ap.add_argument("--themes", default="light,dark")
    a = ap.parse_args()
    site, out = Path(a.site).resolve(), Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    filters = [f.strip() for f in a.pages.split(",") if f.strip()]
    exe = edge()
    work = Path(tempfile.mkdtemp(prefix="joypro-diagrams-"))
    profile = work / "profile"
    n_total = 0
    try:
        for page in sorted(site.rglob("*.html")):
            rel = page.relative_to(site).as_posix()
            if filters and not any(rel == f or rel.startswith(f) for f in filters):
                continue
            blocks = BLOCK.findall(page.read_text(encoding="utf-8"))
            for i, block in enumerate(blocks, 1):
                base = rel.removesuffix(".html").replace("/", "_") + f"-{i}"
                for view in a.views.split(","):
                    for theme in a.themes.split(","):
                        width, window = (860, 940) if view == "desktop" else (360, 520)
                        doc = work / f"{base}-{view}-{theme}.html"
                        doc.write_text(
                            f'<!doctype html><html data-theme="{theme}"><head><meta charset="utf-8">'
                            f'<meta name="viewport" content="width=device-width, initial-scale=1">'
                            f'<link rel="stylesheet" href="{(site / "assets" / "site.css").as_uri()}">'
                            f'<style>body{{margin:0;padding:12px}} main{{padding:0!important}} .content{{width:{width}px;max-width:none}}</style></head>'
                            f'<body data-root="{site.as_uri()}/"><main><div class="content">{block}</div></main>'
                            f'<script src="{(site / "assets" / "vendor" / "mermaid.min.js").as_uri()}"></script>'
                            f'<script src="{(site / "assets" / "site.js").as_uri()}"></script></body></html>', encoding="utf-8")
                        png = out / f"{base}-{view}-{theme}.png"
                        subprocess.run([exe, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                                        f"--user-data-dir={profile}", "--allow-file-access-from-files", f"--window-size={window},2600",
                                        "--virtual-time-budget=6000", f"--screenshot={png}", doc.as_uri()],
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
                        if png.exists():
                            crop(png)
                            n_total += 1
                        else:
                            print("FAILED", png)
        print(f"{n_total} renders in {out}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
