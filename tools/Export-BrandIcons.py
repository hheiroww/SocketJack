"""Export SocketJack's approved transparent master into PNG and ICO assets.

Requires Python 3 and Pillow. NuGet icons also carry a package badge.
"""

import argparse
import hashlib
import json
import struct
from pathlib import Path

from PIL import Image, ImageDraw


PNG_SIZES = (16, 24, 32, 48, 64, 128, 180, 192, 256, 512, 1024)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def load_master(master_path: Path) -> Image.Image:
    with Image.open(master_path) as source:
        if source.width != source.height or "A" not in source.getbands():
            raise ValueError("The master must be a square PNG with an alpha channel.")
        master = source.convert("RGBA")
        master.info.clear()
    if master.getchannel("A").getextrema()[0] != 0:
        raise ValueError("The master must retain a transparent background.")
    return master


def export(master_path: Path, output: Path) -> None:
    master = load_master(master_path)
    output.mkdir(parents=True, exist_ok=True)
    files = []
    for size in PNG_SIZES:
        path = output / f"socketjack-{size}.png"
        if size == 16:
            # Reserve a clear pixel at favicon scale; a subpixel margin alone
            # lets the resampling filter leave a faint alpha halo at the corners.
            small = master.resize((14, 14), Image.Resampling.LANCZOS)
            resized = Image.new("RGBA", (16, 16), (0, 0, 0, 0))
            resized.alpha_composite(small, (1, 1))
        else:
            resized = master.resize((size, size), Image.Resampling.LANCZOS)
        resized.save(path, format="PNG", optimize=True)
        files.append(path)
    logo = output / "socketjack-logo.png"
    master.save(logo, format="PNG", optimize=True)
    files.append(logo)
    icon = output / "favicon.ico"
    # Embed the exact PNG exports, including the small-size transparent margin.
    payloads = [(size, (output / f"socketjack-{size}.png").read_bytes()) for size in ICO_SIZES]
    offset = 6 + 16 * len(payloads)
    with icon.open("wb") as stream:
        stream.write(struct.pack("<HHH", 0, 1, len(payloads)))
        for size, payload in payloads:
            dimension = size if size < 256 else 0
            stream.write(struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset))
            offset += len(payload)
        for _, payload in payloads:
            stream.write(payload)
    files.append(icon)
    manifest = {
        "master": master_path.name,
        "master_sha256": hashlib.sha256(master_path.read_bytes()).hexdigest(),
        "png_sizes": list(PNG_SIZES),
        "ico_sizes": list(ICO_SIZES),
        "files": [
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            }
            for path in files
        ],
    }
    (output / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )
    print(f"Exported {len(files)} icon files and manifest.json.")


def export_package_icons(master_path: Path, repo: Path) -> None:
    size, scale = 256, 4
    master = load_master(master_path).resize((size, size), Image.Resampling.LANCZOS)
    glyph = "#D5C7FF"

    def scaled(box):
        return tuple(value * scale for value in box)

    for package in ("SocketJack", "SocketJack.WPF"):
        badge = Image.new("RGBA", (size * scale, size * scale))
        draw = ImageDraw.Draw(badge)
        draw.ellipse(scaled((164, 164, 253, 253)), fill="#11172B")
        draw.ellipse(scaled((166, 166, 251, 251)), outline="#B6A1F4", width=4 * scale)
        if package == "SocketJack":
            draw.line([scaled(point) for point in ((192, 219), (209, 195), (226, 219))],
                      fill=glyph, width=5 * scale, joint="curve")
            for x, y in ((192, 219), (209, 195), (226, 219)):
                draw.ellipse(scaled((x - 7, y - 7, x + 7, y + 7)), fill=glyph)
            path = repo / "SocketJack" / "SocketJackIcon.png"
        else:
            draw.rounded_rectangle(scaled((186, 191, 231, 225)), radius=5 * scale,
                                   outline=glyph, width=5 * scale)
            draw.line(scaled((188, 202, 229, 202)), fill=glyph, width=4 * scale)
            draw.line(scaled((203, 203, 203, 223)), fill=glyph, width=4 * scale)
            path = repo / "SocketJack.Windows" / "SocketJackWpfIcon.png"
        icon = Image.alpha_composite(master, badge.resize((size, size), Image.Resampling.LANCZOS))
        icon.save(path, format="PNG", optimize=True)
        print(f"Exported {package} icon to {path}.")


if __name__ == "__main__":
    repo = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--master",
        type=Path,
        default=repo / "docs/branding/socketjack-icon-master.png",
    )
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--output", type=Path, help="Write the PNG and ICO size exports to this directory.")
    mode.add_argument("--package-icons", action="store_true", help="Update the checked-in NuGet package icons.")
    args = parser.parse_args()
    if args.package_icons:
        export_package_icons(args.master, repo)
    else:
        export(args.master, args.output)
