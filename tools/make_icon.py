"""Turns assets/AppIcon.png (a square picture, ideally 512x512 or larger) into assets/AppIcon.ico
with every size Windows uses (16 to 256 px).

Run: python3 tools/make_icon.py [input.png] [output.ico]
"""
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]


def main():
    source = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "assets" / "AppIcon.png"
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "assets" / "AppIcon.ico"
    picture = Image.open(source).convert("RGBA")
    if picture.width != picture.height:
        side = min(picture.size)
        left, top = (picture.width - side) // 2, (picture.height - side) // 2
        picture = picture.crop((left, top, left + side, top + side))
    picture.resize((256, 256), Image.LANCZOS).save(out, format="ICO", sizes=SIZES)
    print("wrote", out)


if __name__ == "__main__":
    main()
