# ascii-pack

Build-time converter for ascii-magic pieces. It samples `draw(t)` and writes one
`.mhp` frame pack per piece. MetroHub reads the pack with plain C#. No JavaScript
and no browser engine is needed at run time.

## Setup

```
cd tools/ascii-pack
npm install
```

## Use

```
node pack.mjs --list
node pack.mjs --piece matrix-rain
node pack.mjs --piece rainforest-falls-mist --seconds 6
node pack.mjs --piece matrix-rain --preview 0
```

Options:

| Flag | Meaning |
|---|---|
| `--piece <slug>` | Piece to pack (required) |
| `--seconds N` | Fixed loop length. Default: pick the best seam between 4 s and 12 s |
| `--fps N` | Override `meta.fps` |
| `--out DIR` | Output folder. Default `src/MetroHub/Assets/AsciiArt` |
| `--preview [n]` | Print frame `n` (default 0) as text |
| `--list` | Print every piece slug |

The packer prints the file size, the loop seam score, and the result of a
round-trip check. The check decodes the written file and compares every cell
with the source frame. A failed check stops with exit code 1.

## Format (`MHP1`)

Little-endian. Header:

```
u8[4] magic "MHP1"
u16   version
u16   cols, rows
u16   fps
u16   frameCount
u16   cellAspectMilli    1661 = tall text cell, 1000 = square
u16   paletteCount       then 3 bytes RGB each
u16   glyphCount         then 2 bytes codepoint each
u8    slugLen + utf8
u8    nameLen + utf8
u32   bodyRawLength
u32   bodyGzipLength
      gzip body
```

Body, per frame:

```
u8 1 = full frame, then cells × (u8 glyphId, u8 colorId)
u8 0 = delta frame, then ceil(cells/8) mask bytes, then (u8 glyphId, u8 colorId)
       for each set bit (LSB first)
```

`colorId 0` means default ink (theme color). Ids 1..N index the palette table.

## Limits

- Cell counts above 65,535 are rejected.
- More than 256 glyphs or 255 palette colors are rejected.
- Photo scenes with `meta.look: "dots"` are packed as their block characters.
  The website draws those pieces as canvas halftone circles instead.

## License

Pieces and `lib/kit.js` come from `ascii-magic-art`, MIT licensed. See
`node_modules/ascii-magic-art/LICENSE` and `NOTICE`. Two photo scenes
(Ocean Sunset, Starry Ridge) keep the Unsplash License.
