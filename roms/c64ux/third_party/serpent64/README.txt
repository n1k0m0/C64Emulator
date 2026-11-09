SERPENT 64  v1.0.4
==================
An advanced snake game for the Commodore 64, lovingly coded in 6502
assembly.  (C) 2026 Advancing Development.  MIT licensed.

Enjoying it?  Buy me a coffee:  https://ko-fi.com/advancing
Source code:  https://github.com/AdvancingDevelopment/c64snake


THE GAME
--------
Steer the serpent, eat apples, clear 12 handcrafted arenas that loop
ever faster.  Walls block but never kill: hit one head-on and the
snake waits (losing 1 point per beat) until you steer away; travel
along a wall while pressing into it and the snake slides, snapping
into the first gap that passes.  Only two things end a life: biting
yourself, and the ghosts.

ON THE FIELD
------------
  APPLE      eat to grow and score (2-5 on screen, they wander)
  DIAMOND    50 points, disappears fast
  GHOST      deadly! it slowly hunts your head
  HOURGLASS  slows you down for a while
  X2         doubles food points for a while

End-of-level bonus: 50 x level.  Extra life every 1,000 points.
High scores save to disk when a drive is present.

CONTROLS
--------
  Steer       joystick (port 1 or 2), WASD, or cursor keys
  Start       fire or SPACE
  Pause       fire or SPACE during play (Q quits to the title)
  Help        F1 - works everywhere, even mid-game
  Name entry  type it, or joystick: up/down letter, left/right move,
              fire when done

LOADING
-------
Emulator (VICE etc.):  autostart serpent64.prg, or attach
serpent64.d64 and it will autostart.  Keep the .d64 attached as
drive 8 to keep your high-score table between sessions.

Real hardware:  write serpent64.d64 to disk (or use an SD2IEC /
Ultimate cartridge), then:

    LOAD"*",8,1
    RUN

PAL and NTSC machines are both supported.

CREDITS
-------
Design, code, graphics and music: Advancing Development, 2026.
Built with the ACME cross-assembler; tested under VICE.
