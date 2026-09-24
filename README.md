# mind-control

An observe-and-advise coaching reactor: it consumes the live game-state feed
published by [spectral-sight](../spectral-sight) and prints real-time,
fair-play coaching feedback — where a good player's attention should be on the
minimap, and why. It sends no input to the game or to any device; it only
watches and explains.

Two rules keep it fair:

- **Advises, never acts.** The output is coaching notes (and, optionally, a
  recorded ghost-cursor path for the viewer). Nothing is ever sent back into
  the game.
- **Uses only what the player can see.** The policy reacts to enemies that are
  currently visible on the player's own screen, and to allied deaths (which the
  game announces). It never consumes fog-of-war information — no enemy
  positions in fog, no "seconds since seen", no last-known spots, no level or
  cast sensed through the fog.

Input boundary: SSE feed at `http://127.0.0.1:8723`, wire format in
`spectral-sight/docs/output-format.md` (schema 1).

## Layout

Three layers; the policy is the one that churns and stays pure of I/O:

- `src/MindControl/Feed` — SSE → typed envelopes and events. Frames land in a
  latest-wins mailbox (capacity 1, drop-oldest: stale game state is never
  queued); events, gaps, and connection changes in an ordered notice queue.
- `src/MindControl/Policy` — `(state, event) → a coaching cue`. Testable
  against replayed timelines with no I/O. `AttentionPolicy` is the fair-play
  attention demonstrator; `ExecutionPolicy` speaks only when a shot went wide
  or a bolt found the player standing still (it needs a spectral-sight
  `--coach` run); `CastPolicy` presses the ability a coach would have thrown
  by now ("coach would have pressed Q here"); `CompositePolicy` runs all
  three; `NoOpPolicy` watches and says nothing.
- `src/MindControl/Reactor.cs` — the decision loop and the safety rules: any
  feed doubt (disconnect, gap, lag, fps collapse, silence) pauses coaching
  rather than advising off stale state.

## Dev loop

```powershell
# terminal 1, in the spectral-sight repo:
python tools/replay.py <clip>.jsonl --from 260 --speed 4

# terminal 2, here:
etc/dev.ps1                         # coaching feedback to the console
etc/dev.ps1 -Log data/coaching.log  # also append it to a file
```

`dotnet test` needs nothing running.

The execution-coaching fixture is `data/coach-full-20260902-222718.jsonl`
(local, gitignored): the whole of `Recording 2026-08-30 200315` exported with
`--coach` by spectral-sight's gated build of 2026-09-02, video 142–1121s. Its
counts are in `ExecutionPolicy`'s doc comment, and the measurements behind the
gate in `spectral-sight/docs/aim-bolt-findings.md`. Earlier `--coach` exports
credited casts with bolts that were mostly not the player's shot; nothing
measured on them is to be trusted or preserved. To replay it:

```powershell
# terminal 1, in the spectral-sight repo (lane until ~700s, fights after):
python tools/replay.py ../mind-control/data/coach-full-20260902-222718.jsonl --from 700 --speed 4

# terminal 2, here:
etc/dev.ps1 -- --self Ezreal
```

While it runs it also serves the coaching feedback as SSE at
`http://localhost:8724/stream` (`--serve <port>` to move it, `--serve 0` to
turn it off). The spectral-sight dashboard's COACHING panel subscribes to it:
open `http://127.0.0.1:8723/` and the cues appear next to the event log, with
the ghost's attention drawn as a gold crosshair on the map. The stream is
output-only, like the console.

## Cast coaching

The keyboard half of the demonstration. `CastPolicy` watches the player's own
ability HUD (spectral-sight's `ability` events name the button and print its
cooldown) and the enemies drawn on their screen, and when an ability is known
to be up and a visible enemy has stood inside its range for two seconds
without the player throwing it, the coach presses the key:

```
key[p2]: coach would have pressed Q here: Karma has been in Q range (980 units) for 2.0s with Q up
```

It is deliberately conservative. A slot is only known to be up after its first
cast has been seen with a readable countdown (it may not be skilled before
that); an enemy in fog is not in range of anything; an empty mana bar and a
dead player are silence; and it knows the ranges of only the champions listed
in `AbilityKits` (Ezreal's Q and W today — E is a blink and R is global, and
neither is something to throw at whoever is closest). On the execution fixture
below (`--self Ezreal`, replayed from 140s) it presses Q 16 times and W 22
times in seventeen minutes, and on roughly half of the Q presses the player
pressed the same key inside the next two seconds: the coach is a beat ahead,
not somewhere else. What it does not yet
demonstrate is *where* the coach would have aimed — the ghost's cursor still
belongs to attention, on the minimap.

## Ghost input recording

The ghost's input -- the mouse and keyboard reactions the coach would have
made -- is also appended to `data/ghost.msdr` in the wire format of the
[misdirection](../misdirection) HID bridge, via the
[misdirection-client](submodules/misdirection-client) library's protocol file
(`--record <file>` to move it, `--record none` to turn it off). Each run opens
with a `ScreenSize` frame; every cursor move follows as a `MouseMove`, and
every key the coach presses as a `KeyDown` and `KeyUp` pair. It is a
recording, not a connection: this tool never opens the device. The format
carries no timing, so the trace below remains the record of *when* (key
presses land there too, as `key` lines).

Add `--trace data/ghost-trace.jsonl --self <champion>` and open
`etc/ghost-viewer.html` (self-contained, drag the timeline + trace onto it) to
watch the ghost's cursor over the map, with every glance labeled with its
reason and jumpable from the tick strip.

`etc/minimap-calibrator.html` turns a screenshot of the player's screen into
the exact `--screen`/`--minimap` arguments: paste the screenshot (Ctrl+V),
click the minimap's two corners, copy the line.