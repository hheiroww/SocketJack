"""Render the authored SocketJack marketing SVGs to deterministic PNGs.

Requires Pillow and installed Segoe UI regular/bold fonts. The renderer supports
the small declarative SVG subset used here: rect, line, polyline, circle, text,
and the linked, unchanged monochrome logo. Font files are not redistributed.
"""
import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image, ImageColor, ImageDraw, ImageFont, __version__ as pillow_version


HERE = Path(__file__).resolve().parent
MASTER = HERE.parent / "socketjack-unity-icon-master.png"
ASSETS = {
    "socketjack-icon-160": (160, 160),
    "socketjack-card-420x280": (420, 280),
    "socketjack-cover-1950x1300": (1950, 1300),
    "socketjack-social-1200x630": (1200, 630),
    "socketjack-card-1200x800": (1200, 800),
}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def render(source, output, regular, bold):
    root = ET.parse(source).getroot()
    width, height = int(root.attrib["width"]), int(root.attrib["height"])
    vx, vy, vw, vh = map(float, root.attrib["viewBox"].split())
    if vx != 0 or vy != 0 or abs(width / height - vw / vh) > 0.00001:
        raise ValueError("SVG viewBox must preserve the requested canvas ratio.")
    supersample = 2
    scale = width / vw * supersample
    image = Image.new("RGBA", (width * supersample, height * supersample), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def number(node, key, default=0):
        return float(node.attrib.get(key, default)) * scale

    def color(node, key):
        value = node.attrib.get(key)
        return None if value in (None, "none") else ImageColor.getrgb(value)

    for node in root:
        tag = node.tag.rsplit("}", 1)[-1]
        stroke_width = max(1, round(number(node, "stroke-width", 1)))
        fill, stroke = color(node, "fill"), color(node, "stroke")
        if tag == "rect":
            x, y = number(node, "x"), number(node, "y")
            draw.rectangle((x, y, x + number(node, "width"), y + number(node, "height")), fill=fill, outline=stroke, width=stroke_width)
        elif tag == "line":
            draw.line((number(node, "x1"), number(node, "y1"), number(node, "x2"), number(node, "y2")), fill=stroke, width=stroke_width)
        elif tag == "polyline":
            coords = [float(value) * scale for value in re.split(r"[ ,]+", node.attrib["points"].strip())]
            if len(coords) % 2:
                raise ValueError("Malformed polyline.")
            draw.line(list(zip(coords[::2], coords[1::2])), fill=stroke, width=stroke_width, joint="curve")
        elif tag == "circle":
            x, y, radius = number(node, "cx"), number(node, "cy"), number(node, "r")
            draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=fill, outline=stroke, width=stroke_width)
        elif tag == "text":
            font_path = bold if int(node.attrib.get("font-weight", "400")) >= 600 else regular
            font = ImageFont.truetype(str(font_path), round(number(node, "font-size")))
            text = "".join(node.itertext())
            x, y = number(node, "x"), number(node, "y")
            bounds = draw.textbbox((x, y), text, font=font, anchor="ls")
            if bounds[0] < 0 or bounds[1] < 0 or bounds[2] > image.width or bounds[3] > image.height:
                raise ValueError("Text extends beyond the requested export canvas.")
            draw.text((x, y), text, fill=fill, font=font, anchor="ls")
        elif tag == "image":
            linked = (source.parent / node.attrib["href"]).resolve()
            if linked != MASTER.resolve():
                raise ValueError("Only the approved repository logo can be linked.")
            w, h = round(number(node, "width")), round(number(node, "height"))
            if w != h:
                raise ValueError("The approved square logo cannot be stretched.")
            with Image.open(linked) as original:
                if original.width != original.height:
                    raise ValueError("The approved logo must remain square.")
                # Preserve the full source canvas, proportions and alpha; only resize it.
                logo = original.convert("RGBA").resize((w, h), Image.Resampling.LANCZOS)
            image.alpha_composite(logo, (round(number(node, "x")), round(number(node, "y"))))
        else:
            raise ValueError("Unsupported SVG element: " + tag)
    final = image.resize((width, height), Image.Resampling.LANCZOS).convert("RGB")
    final.save(output, format="PNG", optimize=True)
    return width, height


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--font-regular", type=Path, default=Path("C:/Windows/Fonts/segoeui.ttf"))
    parser.add_argument("--font-bold", type=Path, default=Path("C:/Windows/Fonts/segoeuib.ttf"))
    args = parser.parse_args()
    if not args.font_regular.is_file() or not args.font_bold.is_file():
        raise ValueError("Install Segoe UI regular/bold or provide their font paths explicitly.")
    args.output.mkdir(parents=True, exist_ok=True)
    exports = []
    for stem, dimensions in ASSETS.items():
        source, output = HERE / (stem + ".svg"), args.output / (stem + ".png")
        if render(source, output, args.font_regular, args.font_bold) != dimensions:
            raise ValueError("Export dimensions differ from the required image size.")
        exports.append({"file": output.name, "width": dimensions[0], "height": dimensions[1],
                        "bytes": output.stat().st_size, "sha256": digest(output),
                        "svg": source.name, "svg_sha256": digest(source)})
    manifest = {"method": "Deterministic SVG composition and PNG export",
                "logo": "../socketjack-unity-icon-master.png", "logo_sha256": digest(MASTER),
                "logo_transform": "Uniform scaling of the complete approved source canvas with alpha preserved",
                "fonts": [{"file": font.name, "sha256": digest(font)} for font in (args.font_regular, args.font_bold)],
                "renderer": "Pillow " + pillow_version, "files": exports}
    (args.output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"exports": len(exports), "total_png_bytes": sum(item["bytes"] for item in exports)}))


if __name__ == "__main__":
    main()
