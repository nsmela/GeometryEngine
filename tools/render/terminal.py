"""Render an ANSI-coloured terminal capture into a PNG."""
import re
import sys
from pathlib import Path

import matplotlib
from PIL import Image, ImageDraw, ImageFont

FONT_DIR = Path(matplotlib.get_data_path()) / "fonts" / "ttf"
REGULAR = ImageFont.truetype(str(FONT_DIR / "DejaVuSansMono.ttf"), 20)
BOLD = ImageFont.truetype(str(FONT_DIR / "DejaVuSansMono-Bold.ttf"), 20)

BG = (24, 26, 31)
CHROME = (38, 41, 48)
PALETTE = {
    0: (206, 210, 218),
    31: (240, 113, 120),
    32: (126, 208, 130),
    33: (226, 192, 106),
    36: (110, 190, 214),
    90: (120, 126, 138),
}
ANSI = re.compile(r"\x1b\[([0-9;]*)m")


def spans(line):
    """Split one line into (text, colour, bold) runs."""
    out, colour, bold, cursor = [], 0, False, 0
    for match in ANSI.finditer(line):
        if match.start() > cursor:
            out.append((line[cursor:match.start()], colour, bold))
        for code in (match.group(1) or "0").split(";"):
            code = int(code or 0)
            if code == 0:
                colour, bold = 0, False
            elif code == 1:
                bold = True
            elif code in PALETTE:
                colour = code
        cursor = match.end()
    if cursor < len(line):
        out.append((line[cursor:], colour, bold))
    return out


def render(capture_path, png_path, title, max_lines=None, head=None, tail=None):
    raw = Path(capture_path).read_text(errors="replace").split("\n")
    if head is not None and tail is not None and len(raw) > head + tail:
        elided = len(raw) - head - tail
        raw = raw[:head] + [f"\x1b[90m      ... {elided} lines elided ...\x1b[0m"] + raw[-tail:]
    if max_lines:
        raw = raw[:max_lines]

    char_w = REGULAR.getlength("M")
    line_h = 26
    pad, bar = 26, 44
    width = int(char_w * 104) + pad * 2
    height = bar + pad + line_h * len(raw) + pad

    image = Image.new("RGB", (width, height), BG)
    draw = ImageDraw.Draw(image)

    draw.rectangle([0, 0, width, bar], fill=CHROME)
    for i, dot in enumerate([(255, 95, 86), (255, 189, 46), (39, 201, 63)]):
        draw.ellipse([20 + i * 22, bar // 2 - 7, 34 + i * 22, bar // 2 + 7], fill=dot)
    draw.text((110, bar // 2 - 10), title, font=REGULAR, fill=(150, 156, 168))

    y = bar + pad
    for line in raw:
        x = pad
        for text, colour, bold in spans(line):
            draw.text((x, y), text, font=BOLD if bold else REGULAR, fill=PALETTE[colour])
            x += char_w * len(text)
        y += line_h

    image.save(png_path)
    print(f"wrote {png_path}")


if __name__ == "__main__":
    render(sys.argv[1], sys.argv[2], sys.argv[3],
           head=int(sys.argv[4]) if len(sys.argv) > 4 else None,
           tail=int(sys.argv[5]) if len(sys.argv) > 5 else None)
