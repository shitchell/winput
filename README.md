# winput

Drive the Windows desktop from a WSL terminal. Run it over ssh and whatever you
type goes to whichever Windows window has focus — a keyboard (and mouse) for the
machine across the room.

```
ssh you@your-wsl-box
winput
```

## How it works

```
your terminal ──ssh──> winput (node, raw tty) ──pipe──> java.exe RobotServer ──> Windows
```

`winput` puts the terminal in raw mode, decodes keystrokes and xterm mouse
reports, and writes a one-line-per-event protocol into a long-lived `java.exe`
running `java.awt.Robot`. Robot synthesises real Windows input events, so
anything that accepts keyboard input accepts these.

The JVM starts once per session, so each keystroke costs a pipe write — the only
latency you feel is your own ssh round trip.

### Why there is no daemon

WSL→Windows interop works from a non-interactive ssh session, and a Windows
process launched that way still reaches the interactive desktop (verified: it
can read the pointer position and both monitors' geometry). So the ssh-side
process can spawn the JVM itself. No background service, nothing to keep alive.

## winput — interactive

```
winput [options]

  -s, --screen N     screen index for absolute mouse (default: primary)
  -M, --no-mouse     do not capture the mouse
  -r, --relative     start in relative mouse mode
  -A, --all-screens  map the terminal to every monitor at once
      --sens N       pixels per cell in relative mode (default 12)
  -E, --no-echo      do not echo what is sent
  -p, --prefix SPEC  escape key; repeatable (default: C-])
  -d, --delay MS     inter-event delay on the Windows side (default 8)
  -K, --debug-keys   print what your terminal sends, and exit
  -f, --focus TEXT   focus the window matching TEXT before starting
```

Everything you type goes to Windows — including Ctrl-C. To talk to `winput`
itself, press the prefix (**Ctrl-]**) then:

| key | does |
|-----|------|
| `q` or `.` | quit |
| `?` | help |
| `m` | toggle mouse capture |
| `r` | toggle relative / absolute mouse |
| `s` | cycle target screen |
| `Tab` / `Shift-Tab` | next / previous window |
| `Right` / `Left` | move the focused window to the next / previous monitor |
| `w` | pick a window from a numbered list |
| `f` | show which window currently has focus |
| the prefix again | send the prefix key itself |

## Which window am I typing into?

Robot types into whatever has **focus**, and a fullscreen window on another
monitor can look active while something else actually holds focus. So winput
prints the target on startup:

```
  typing into: WindowsTerminal - Ubuntu-20.04   (Ctrl-] Tab to switch)
```

Switch with `Ctrl-] Tab` (and `Shift-Tab` to go back), or `Ctrl-] w` for a
numbered picker. `Ctrl-] f` re-checks the current focus at any time.

From the command line:

```sh
winkeys --windows                       # list switchable windows
winkeys --focus Ubuntu                  # focus by title or process substring
winkeys --focus Ubuntu 'ls -la' -k enter  # focus, then type into it
winput --focus Ubuntu                   # start already pointed at it
```

Window control is a small C# helper (`src/WinCtl.cs`) compiled once with the
.NET Framework `csc.exe` that ships with Windows — no dependency to install.
It does the `AttachThreadInput` dance, because Windows refuses
`SetForegroundWindow` from a process that does not already own the foreground.

### Changing the prefix

`--prefix` accepts `C-]`, `ctrl+x`, `^x`, `M-x`, `alt+x`, `f1`–`f12`, or a bare
character meaning `Ctrl-<char>`. Pass it more than once to have several:

```sh
winput -p f12                # F12 is the prefix
winput -p C-g                # Ctrl-G
winput -p C-] -p f12         # either one
```

Whatever is *not* the prefix gets forwarded to Windows normally, so `-p f12`
frees up Ctrl-] for the far side.

Esc is rejected on purpose: terminals encode Alt-<char> as Esc followed by that
character, so an Esc prefix is ambiguous and would misfire.

### When the prefix does not reach winput

Terminals and multiplexers can intercept keys before winput sees them. To find
out what actually arrives:

```sh
winput --debug-keys
```

That starts no JVM and sends nothing — it just prints the raw bytes and the
decoded event for every key, marking the one it would treat as the prefix:

```
  1d                       chord ctrl+]  <- prefix
  1b 5b 32 34 7e           key f12
  68 69                    text "hi"
```

If pressing your prefix prints nothing at all, something upstream swallowed it;
pick a key that does show up. Ctrl-C exits.

What gets forwarded: printable text (UTF-8), Enter/Tab/Backspace/Esc/Delete,
arrows, Home/End/PgUp/PgDn, F1–F12, `Ctrl-<letter>`, `Alt-<char>`, Shift-Tab,
and modified arrows such as Ctrl-Right.

### Mouse

Mouse capture needs a terminal that speaks xterm mouse reporting (Windows
Terminal, iTerm2, kitty, most Android/iOS ssh clients). It is on by default.

The terminal window maps onto the screen proportionally: a cell at 50% across
your terminal puts the pointer at 50% across the target, offset by that
monitor's origin. On an 80-column terminal, column 40 lands near x=632 of a
1280-wide screen (the half-cell offset centres it in the cell).

Terminals report *cells*, not pixels, so precision is bounded by your terminal
size:

- **absolute** (default) — the terminal window *is* the screen. A cell is
  `screen_width / columns` px, so ~10px on a 160-column terminal, ~19px on an
  80-column one. Good for hitting buttons.
- **relative** (`Ctrl-] r`) — cell movement becomes a delta of `--sens` px, so
  repeated small motions reach any pixel. Use this when absolute is too coarse.

- **all screens** (`-A`, or `Ctrl-] s` to cycle onto it) — the terminal maps to
  the union of every monitor instead of one, so the pointer can cross between
  them. This is what you want for dragging a window to another display.
  Note the union includes dead space when monitors differ in size or alignment;
  the pointer simply will not go there.

Wheel and all three buttons are forwarded. Drags work (press and release are
sent separately).

## Moving a window to another monitor

`Ctrl-] Right` / `Ctrl-] Left` move the focused window one monitor along a
rotation, or from the shell:

```sh
winkeys --monitors            # monitors in rotation order
winkeys --move-window 1       # focused window to the next monitor
winkeys --move-window -1      # previous
```

This deliberately does **not** use Windows' Win+Shift+Arrow. That shortcut is
*direction*-based, so it does nothing when monitors are stacked vertically
rather than side by side (and Win+Shift+Up/Down are already maximise/minimise,
so there is no vertical equivalent). Instead the window is moved with
`SetWindowPos` through a rotation sorted top-to-bottom then left-to-right,
which behaves the same whatever the arrangement. Position and size are kept
proportional to the target's work area, and a maximised window is restored,
moved, and re-maximised.

### Two DPI traps this has to work around

Both cost real debugging time, so they are worth knowing:

1. **A DPI-unaware process gets its coordinates rewritten.** On a mixed-DPI
   setup (here: 125% primary, 100% external) Windows silently rescales what you
   pass to `SetWindowPos`, so a move lands at the wrong size. WinCtl calls
   `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` so it works in real
   pixels.
2. **The app resizes itself afterwards.** Crossing to a different-DPI monitor
   makes Windows send `WM_DPICHANGED`, and the application then applies its own
   sizing, overriding the placement. So the move places the window, waits for
   that to settle, and re-applies — the second pass triggers no DPI change
   because the window is already on the target monitor.

Because of trap 1, `winkeys --monitors` reports **physical** pixels while
`winkeys --screens` reports the **DPI-scaled** space the mouse uses. On a
125%-scaled display the same monitor shows as 1920x1080 in one and 1536x864 in
the other. That is expected: mouse positioning goes through Robot's scaled
space, window geometry goes through Win32's physical space.

### Motion is coalesced

Pointer position is idempotent, so only the newest one matters. A burst of
motion collapses to its final value rather than queueing — in testing, 60
motion events in one chunk became 2 sends. Mouse moves also skip the
inter-event delay that keystrokes need (that delay caps throughput near 87
moves/sec, which a fast sweep across a wide terminal easily outruns; the
backlog then replays for far longer than the movement took).

## winkeys — one-shot

For scripts, and for when you just want to poke the machine once.

```sh
winkeys 'tt\n'                            # type tt then Enter
winkeys -c alt+tab                        # a chord
winkeys --after 3000 'hello' -k enter     # wait 3s, then type
winkeys -m 900,-600 -b 1                  # move to a pixel and click
winkeys --screens --pointer               # inspect geometry
echo "$text" | winkeys -                  # type stdin
```

Actions apply in the order given: bare text, `-k/--key`, `-c/--chord`,
`-m/--move X,Y`, `-b/--click N`, `-w/--wheel N`, `-S/--sleep MS`.

## The protocol

`src/RobotServer.java` reads these on stdin, one per line, and answers `ok`,
`err <msg>`, or `= <data>` lines followed by `ok`:

| line | meaning |
|------|---------|
| `t <text>` | type literal text |
| `k <key> [<key>…]` | tap named keys |
| `c <mod>+<mod>+<key>` | chord, e.g. `c ctrl+shift+t` |
| `d <key>` / `u <key>` | key down / up |
| `m <x> <y>` | move pointer (virtual desktop px) |
| `b <1\|2\|3> <click\|down\|up\|dbl>` | mouse button |
| `w <n>` | wheel notches, negative scrolls up |
| `p <text>` | paste via clipboard |
| `s <ms>` | sleep |
| `?screens` / `?pointer` | query geometry |
| `q` | quit |

Responses pair with commands in order, which is what lets a client know the
server has actually finished before it exits.

### Typing non-ASCII

`t` maps US-ASCII to real key events. Anything else (`★`, `ü`, emoji) is batched
and delivered through the clipboard, with the previous clipboard contents saved
and restored afterwards.

### Coordinates

Robot uses the full virtual desktop, including negative coordinates for monitors
placed above or left of the primary. `winkeys --screens` prints the layout:

```
screen 0 \Display0 1536x864 at 0,0 (primary)
screen 1 \Display1 1920x1080 at 0,-1080
```

## Requirements

- A Windows JDK or JRE. Found automatically under `Program Files`; override with
  `WINPUT_JAVA`. A JDK is preferred — the server is precompiled to
  `%LOCALAPPDATA%\winput` and rebuilt only when the source hash changes.
- Node 18+ on the WSL side.

## Development

`WINPUT_DRYRUN=1` swaps the JVM for a mock that echoes the protocol to stderr,
so the input parsing can be exercised without touching the desktop:

```sh
{ sleep 1; printf 'hi\r\x1b[A\x1dq'; sleep 0.5; } \
  | WINPUT_DRYRUN=1 script -q -c './bin/winput --no-echo' /dev/null | grep SENT
```

## Caveats

- Robot drives whatever has focus. It cannot target a specific window, and it
  cannot type into an elevated (admin) window unless the JVM is elevated too.
- The key map assumes a US layout.
- Ctrl-H is indistinguishable from Backspace in most terminals; Backspace wins.
- A locked workstation will not accept input — unlock it first.
