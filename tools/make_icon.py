from __future__ import annotations

import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"


def find_chrome() -> str:
    candidates = [
        shutil.which("chrome"),
        shutil.which("msedge"),
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    ]
    for candidate in candidates:
        if candidate and Path(candidate).exists():
            return str(candidate)
    raise RuntimeError("Chrome or Edge was not found for SVG rendering")


def render_svg(svg_path: Path, output_png: Path, size: int = 1024) -> None:
    svg = svg_path.read_text(encoding="utf-8")
    html = f"""<!doctype html>
<meta charset="utf-8">
<style>
html, body {{
  margin: 0;
  width: {size}px;
  height: {size}px;
  overflow: hidden;
  background: transparent;
}}
svg {{
  display: block;
  width: {size}px;
  height: {size}px;
  fill: #111827;
}}
</style>
{svg}
"""
    chrome = find_chrome()
    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = Path(tmp)
        html_path = tmp_path / "icon.html"
        raw_png = tmp_path / "icon-raw.png"
        profile = tmp_path / "chrome-profile"
        html_path.write_text(html, encoding="utf-8")
        subprocess.run(
            [
                chrome,
                "--headless=new",
                "--disable-gpu",
                "--hide-scrollbars",
                "--no-first-run",
                "--no-default-browser-check",
                f"--user-data-dir={profile}",
                f"--window-size={size},{size}",
                f"--screenshot={raw_png}",
                html_path.as_uri(),
            ],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        image = Image.open(raw_png).convert("RGBA")

    # Some Chromium builds flatten transparent screenshots onto white.
    pixels = image.load()
    for y in range(image.height):
        for x in range(image.width):
            r, g, b, a = pixels[x, y]
            if a == 255 and r > 246 and g > 246 and b > 246:
                pixels[x, y] = (255, 255, 255, 0)

    bbox = image.getbbox()
    if bbox:
        image = image.crop(bbox)

    canvas = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    draw = ImageDraw.Draw(canvas)
    margin = 48
    draw.rounded_rectangle(
        (margin, margin, size - margin, size - margin),
        radius=140,
        fill=(255, 255, 255, 255),
        outline=(222, 226, 232, 255),
        width=10,
    )
    image.thumbnail((size - 220, size - 220), Image.Resampling.LANCZOS)
    x = (size - image.width) // 2
    y = (size - image.height) // 2
    canvas.alpha_composite(image, (x, y))
    canvas.resize((256, 256), Image.Resampling.LANCZOS).save(output_png)


def make_ico(png_path: Path, ico_path: Path) -> None:
    image = Image.open(png_path).convert("RGBA")
    image.save(ico_path, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", default=r"C:\Users\NhanAZ\Downloads\fan.svg")
    args = parser.parse_args()

    source = Path(args.source)
    if not source.exists():
        raise FileNotFoundError(source)

    ASSETS.mkdir(exist_ok=True)
    asset_svg = ASSETS / "fan.svg"
    if source.resolve() != asset_svg.resolve():
        shutil.copy2(source, asset_svg)
    png_path = ASSETS / "fan.png"
    ico_path = ASSETS / "fan.ico"
    render_svg(source, png_path)
    make_ico(png_path, ico_path)
    print(f"Wrote {png_path}")
    print(f"Wrote {ico_path}")


if __name__ == "__main__":
    main()
