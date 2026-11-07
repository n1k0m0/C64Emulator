# Open Basic 64

`basic.asm`, `for_float.asm`, and `float.asm` form an independently written
8 KiB 6510 BASIC ROM. It boots from the word at `$A000` and uses Open Kernal
64 for screen, keyboard, file I/O, number formatting, and math functions.
The builder uses the repository's small 6502 assembler and emits a symbol map.
No original BASIC source or binary is needed.

The implementation stores tokenized linked program lines at `$0801`. Its
token values follow the public BASIC V2 file format. `float.asm` implements
five-byte binary floating-point values with a 32-bit significand. Numeric
expressions support decimal literals (including `E` notation), addition,
subtraction, multiplication, division, exponentiation, comparisons, `ABS`,
`SGN`, `INT`, `SQR`, `RND`, `LOG`, `EXP`, `COS`, `SIN`, `TAN`, and `ATN`.
Normal numeric variables use the public five-byte C64 layout; `%` variables
remain signed 16-bit integers. `PRINT` and `STR$` emit up to nine significant
digits in fixed or scientific notation. `VAL` parses fractional and exponent
notation from literal and variable strings, including `STR$` results. Numeric
`INPUT` and `READ` accept fractions.
Program control includes
`IF/THEN`, `GOTO`, `GOSUB/RETURN`, and up to four nested `FOR/NEXT` frames
with floating-point limits and steps. Simple literal and `STR$` string
assignment, `INPUT`, and numeric/string
`READ/DATA/RESTORE` are available. Disk and file commands use the public
KERNAL jump table.

The fixed jump stubs at `$A010` through `$A034` connect the BASIC RAM
vectors and selected KERNAL private entry points to this ROM. These are
Open Basic 64 interfaces, not a promise of byte-for-byte private Commodore
ROM compatibility.

The numeric behavior is functional but not byte-exact in all cases. The
optional audit currently matches 45/47 selected five-byte results and 18/18
printed numeric strings. A separate deterministic 150-case sample matches
133 results: 90/90 binary operations, 25/42 math-function cases, and 18/18
comparisons. The remaining selected differences are `SQR(.01)` and
`EXP(-1)`; broader tests reveal rounding differences in transcendental
functions and extreme values. A signed 40-case SIN small-angle holdout
matches byte-for-byte after turn quantization, while larger angles and
several TAN results still differ. Decimal literals retain a rounding bit
until variable storage. Some zero-exponent mantissa payloads still differ
from the reference. `RND` uses a five-byte seed, byte permutation, and
multiply/add recurrence; five tested 64-step sequences match exactly.
Other seeds, longer sequences, and timing-dependent `RND(0)` remain to be
checked.
Addresses are still 16-bit, and general string expressions, arrays, several
functions, and statements remain unimplemented. A successful boot or
tokenization of a program does not imply that the program can run. Run
`--verify-open-roms` as documented in
[the ROM overview](../README.md) for the supported behavior.
