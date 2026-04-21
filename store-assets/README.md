# Store Assets

Vector (SVG) assets for Wahlberg's store listings. All files are scalable and ready for export to PNG at any resolution.

## Logos (`logos/`)

| File | Size | Use |
|------|------|-----|
| `icon-300x300.svg` | 300×300 | Microsoft Store square logo |
| `icon-512x512.svg` | 512×512 | Google Play high-res icon |
| `icon-1024x1024.svg` | 1024×1024 | Apple App Store icon |

All logos use the app's brand colours (`#1B3A5C` background, white Markdown icon) with rounded corners matching platform conventions.

## Screenshots (`screenshots/`)

All screenshots are **1280×800** (16:10), suitable for Windows Store, Google Play feature graphic (crop to 1024×500), and Mac App Store.

| File | Scene |
|------|-------|
| `screenshot-1-welcome.svg` | Welcome / empty state with CTA button |
| `screenshot-2-reading.svg` | Document reading with table of contents |
| `screenshot-3-tabs.svg` | Multiple tabs open (CHANGELOG view) |
| `screenshot-4-settings.svg` | Settings panel — theme selection & customisation |

## Exporting to PNG

Use any of the following tools to rasterise the SVGs:

```bash
# Inkscape (recommended)
inkscape --export-type=png --export-width=1280 screenshot-2-reading.svg

# rsvg-convert
rsvg-convert -w 1280 screenshot-2-reading.svg -o screenshot-2-reading.png

# ImageMagick
magick -density 144 screenshot-2-reading.svg screenshot-2-reading.png
```
