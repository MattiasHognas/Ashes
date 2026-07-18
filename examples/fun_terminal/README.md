# Ping Pong Cannon

A colorful turn-based terminal duel: you and the computer sit at opposite ends
of a table and lob a ball over the net with a cannon. Type an angle and a power,
watch the ANSI-rendered arc, and land the ball in the opponent's paddle zone to
score. First to 3 points wins. Real-time Pong needs raw-mode keyboard input the
runtime does not expose, so this is the turn-based artillery cousin — with the
net kept firmly in the middle.

The example leans on three stdlib pillars:

- `Ashes.Math` — the whole flight is `sin`/`cos`/gravity integration
  (`Physics.ash`), the per-round wind is a deterministic pseudo-random value
  built from `sin`-hash fractions, and the computer aims by inverting the
  projectile range formula (`idealPower`) with an error that shrinks each round.
- `Ashes.Regex` — player input is parsed with a compiled PCRE2 pattern
  (`^\s*(\d{1,2})\s+(\d{1,3})\s*$`) via `captures`, then range-checked.
- ANSI art — `Ansi.ash` builds the escape character from byte 27 with
  `Ashes.Bytes` (string literals have no `\x1b` escape), and everything from the
  rainbow logo to the trajectory trail is colored with it.

The screen state is pure: `Game.renderBoard` folds the trajectory trail, net,
and paddles into a string, and the game loop threads scores and round number
through recursion, reading one line per turn with `Ashes.IO.readLine`.

## Play

```sh
cd examples/fun_terminal
dotnet run --project ../../src/Ashes.Cli -- compile --project ashes.json
./out/ping-pong-cannon
```

Enter shots as `angle power`, for example `45 70`. Angles run 10-80 degrees,
power 10-99, `q` quits. Wind pushes the ball mid-flight and changes every
round — the score line tells you which way it blows. The computer's aim starts
sloppy and sharpens as the rally goes on, and it ignores the wind, which is
sometimes fatal for it.

## Run the tests

```sh
cd examples/fun_terminal
dotnet run --project ../../src/Ashes.Cli -- run --project ashes-test.json
```

The tests are pure: regex parsing and range validation, net clearance and net
collisions at known shots, wind determinism, late-round computer accuracy, and
board rendering.
