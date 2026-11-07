# Open Character Rom 64

The 4 KiB image is built from the original pixel drawings in [build.py](build.py).
It contains two 128-glyph sets; each set has a second 128-glyph inverted half.
Each glyph occupies eight bytes, one byte per row. The letter and digit forms
are a new 5×7 design within each 8×8 cell. Graphics are drawn from lines and
blocks. No original ROM image is a build input.

Every visible screen-code position in both 128-glyph sets now has a drawing.
Codes 32 and 96 are deliberately blank spaces. The inverse halves are exact
bitwise inverses of the corresponding glyphs. The thin 5×7 letters retain
their existing appearance; the artwork is intentionally distinct from the
Commodore character ROM. The full four-ROM headless check for this coverage
change is in `../../artifacts/character-coverage-v1/headless.log`.

Build both open ROMs with `python roms/build.py` from the repository root.
See [the ROM overview](../README.md) for emulator use and compatibility limits.
