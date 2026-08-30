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
  attention demonstrator; `NoOpPolicy` watches and says nothing.
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

Add `--trace data/ghost-trace.jsonl --self <champion>` and open
`etc/ghost-viewer.html` (self-contained, drag the timeline + trace onto it) to
watch the ghost's cursor over the map, with every glance labeled with its
reason and jumpable from the tick strip.

`etc/minimap-calibrator.html` turns a screenshot of the player's screen into
the exact `--screen`/`--minimap` arguments: paste the screenshot (Ctrl+V),
click the minimap's two corners, copy the line.

## License & Disclaimer

**INTERNAL USE ONLY – Product Development**

This repository is proprietary and restricted to authorized internal product-development use by RIOT GAMES only.

- Full terms: [LICENSE](LICENSE.md)
- Reinforcing notice: [DISCLAIMER](DISCLAIMER.md)

**Prohibited uses include** (but are not limited to) creating, distributing, or using any game cheats, hacks, bots, trainers, or any activity that facilitates cheating or violates the terms of service of any video game.

Unauthorized use is strictly forbidden and automatically terminates all rights under the license.

For questions: support@riotgames.com