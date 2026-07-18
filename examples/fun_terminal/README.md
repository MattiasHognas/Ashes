# Terminal Pong

Real-time Pong against the computer, drawn with ANSI escapes in an ordinary
terminal. The left paddle is yours; the right paddle tracks the ball with a
capped speed and can be beaten. First to 5 points wins, `q` (or `Ctrl-C`)
quits.

The example is built on `Ashes.Console`, the raw terminal-input module:

- `enableRawInput` / `restoreInput` switch stdin to raw mode (press-by-press
  delivery, no echo) and back. When stdin is not a terminal — a pipe, a test
  harness — `enableRawInput` returns `false` and the game exits with a hint
  instead of fighting the pipe.
- `pollInput(timeoutMs)` waits up to one frame budget for input and returns
  whatever bytes arrived. Arrow keys arrive as `ESC [ A`/`ESC [ B`, mouse
  motion arrives as SGR `ESC [ < b ; x ; y M` sequences (opted into by
  printing `ESC [ ? 1003 h` and `ESC [ ? 1006 h`), and decoding both is
  ordinary pure string processing in `Input.ash` — including carrying a
  partial escape sequence over to the next frame.
- `monotonicMillis` paces the loop: each frame collects input until the 33 ms
  deadline, then steps the simulation with a fixed timestep.

The game state is a record threaded through a pure step function
(`Physics.ash`): ball integration, wall bounces, paddle english, deterministic
serve angles from a `sin`-hash, and the chasing computer paddle. `Game.ash`
folds the state into one ANSI frame string that `Main.ash` writes over the
previous frame with a home-cursor escape — no per-cell cursor movement, no
flicker. The alternate screen buffer keeps your shell scrollback clean.

## Play

```sh
cd examples/fun_terminal
dotnet run --project ../../src/Ashes.Cli -- compile --project ashes.json
./out/terminal-pong
```

Move with `w`/`s`, the arrow keys, or the mouse. The score line is at the top;
the game announces the winner on exit and restores your terminal mode.

## Run the tests

```sh
cd examples/fun_terminal
dotnet run --project ../../src/Ashes.Cli -- test --project ashes-test.json
```

The tests are pure: escape-sequence decoding (keys, mouse, partial and unknown
sequences), paddle movement from key and mouse events, wall bounces, paddle
returns, scoring on a miss, computer-paddle tracking, the win condition, and
board rendering.
