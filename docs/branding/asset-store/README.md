# SocketJack marketing images

Editable SVG compositions use the approved monochrome SocketJack logo at
`../socketjack-unity-icon-master.png`. The full logo canvas, proportions and alpha
are preserved. Colors are charcoal and neutral white; the visual treatment uses
simple connection lines. Copy is limited to the SocketJack name, “Make your game
multiplayer” and “Physics / UDP / Rooms”. The small icon uses the logo alone.

| Source | PNG dimensions | Intended use |
| --- | --- | --- |
| `socketjack-icon-160.svg` | 160 × 160 | Small listing icon |
| `socketjack-card-420x280.svg` | 420 × 280 | Listing card |
| `socketjack-cover-1950x1300.svg` | 1950 × 1300 | Cover image |
| `socketjack-social-1200x630.svg` | 1200 × 630 | Social preview |
| `socketjack-card-1200x800.svg` | 1200 × 800 | Larger listing/media image |

Export PNGs to an owned output directory with Python 3 and Pillow:

```powershell
python docs/branding/asset-store/export.py --output <owned-output-directory>
```

The exporter reads these SVGs, renders their supported elements at twice the
target resolution and resamples once. It uses installed Segoe UI regular/bold
fonts; explicit font paths can be supplied through `--font-regular` and
`--font-bold`. Font files are not included. SVGs also reference Segoe UI with a
sans-serif fallback for normal viewing.

`manifest.json` records PNG dimensions, hashes, source SVG hashes and the logo
hash. Keep exports and their review receipts outside the source checkout until
their publication destination has been reviewed. These are listing illustrations;
actual game screenshots and workload acceptance evidence are separate assets.
