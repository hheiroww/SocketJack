# SocketJack icon

The canonical asset is `socketjack-icon-master.png`: a transparent, text-free
square socket with eight contacts, a flat dark-blue frame, and a restrained
diagonal fade from purple at the upper left to dark blue at the lower right.
The socket recess remains opaque. There are no bevels or metallic effects.

The package and README images use the same 256-pixel export in
`SocketJack/SocketJackIcon.png` and `SocketJack.Windows/SocketJackWpfIcon.png`.
Preserve the alpha channel, square canvas, proportions, and transparent margin.

To regenerate the PNG sizes and multi-resolution favicon with Python and Pillow:

```sh
python tools/Export-BrandIcons.py --output artifacts/brand-icons
```

The export includes 16, 24, 32, 48, 64, 128, 180, 192, 256, 512, and 1024-pixel
PNGs, a favicon containing 16 through 256-pixel images, and a file-hash manifest.
The exporter changes dimensions and formats only; the master is the design source.
