"""Export SocketJack's approved transparent master into PNG and ICO assets.

Requires Python 3 and Pillow. Only dimensions and file formats are changed.
"""

import argparse
import hashlib
import json
import struct
from pathlib import Path

from PIL import Image


PNG_SIZES = (16, 24, 32, 48, 64, 128, 180, 192, 256, 512, 1024)
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def export(master_path: Path, output: Path) -> None:
    with Image.open(master_path) as source:
        if source.width != source.height or "A" not in source.getbands():
            raise ValueError("The master must be a square PNG with an alpha channel.")
        master = source.convert("RGBA")
        master.info.clear()
    if master.getchannel("A").getextrema()[0] != 0:
        raise ValueError("The master must retain a transparent background.")
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


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--master",
        type=Path,
        default=Path(__file__).resolve().parents[1]
        / "docs/branding/socketjack-icon-master.png",
    )
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    export(args.master, args.output)
