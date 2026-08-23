# qsc — window-only screen cropper

Captures a specific window (or a region of it) to PNG. Windows; needs .NET 8+ runtime. Build: `build.cmd` → `out\qsc.exe`.

## Usage

```
qsc --list                                   find PIDs:  PID X Y W H TITLE
qsc shot.png --pid <pid>                     whole window → PNG
qsc shot.png <pid> [x] [y] [w] [h]           positional form
qsc shot.png --pid <pid> --x 0 --y 245 --w 430 --h 390
qsc shot.png --pid <pid> --scale 0.5         half-size capture
qsc --pid <pid>                              print bounds only:  X Y W H
```

`x y w h` = crop offset/size from the window's top-left (window-relative, not screen coords); both `w` and `h` > 0 to crop, omit for whole window. Prints `SAVED <path> <w>x<h> WIN ...`.

Exit codes: 0 ok · 2 bad args · 3 no process/window · 4 capture/IO error. See `qsc --help`.
