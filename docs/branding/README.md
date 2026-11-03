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

## Unity package variant

`socketjack-unity-icon-master.png` preserves the SocketJack socket mark with a
Unity-inspired monochrome palette: a charcoal frame and recess, light neutral
body and eight light contacts. It contains no Unity mark or text. The NuGet icon
is the 256-pixel export at `SocketJack.Unity/SocketJackUnityIcon.png`; the core
and WPF packages retain their purple/blue icon.

```sh
python tools/Export-BrandIcons.py --master docs/branding/socketjack-unity-icon-master.png --output artifacts/unity-brand-icons
```
