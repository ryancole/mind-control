# mind-control

A reactor: consumes the live game-state feed published by
[spectral-sight](../spectral-sight) and drives the
[misdirection](../misdirection) USB input device to control a machine playing
League of Legends VODs.

Both boundaries are specified elsewhere and pinned here by tests:

- **Input** — SSE feed at `http://127.0.0.1:8723`, wire format in
  `spectral-sight/docs/output-format.md` (schema 1).
- **Output** — 8-byte binary frames over a PL2303 serial link (115200 8N1),
  spec in `misdirection/PROTOCOL.md`. The encoder is pinned byte-for-byte
  against `protocol-vectors.json`; refresh the local copy with
  `etc/sync-vectors.ps1`.

## Layout

Three layers; the policy is the one that churns and stays pure of I/O:

- `src/MindControl/Feed` — SSE → typed envelopes and events. Frames land in a
  latest-wins mailbox (capacity 1, drop-oldest: stale game state is never
  queued); events, gaps, and connection changes in an ordered notice queue.
- `src/MindControl/Policy` — `(state, event) → intents`. Testable against
  replayed timelines with no I/O. Currently `NoOpPolicy`.
- `src/MindControl/Device` — intents → wire frames, HID usage mapping, the
  serial link, and the decoder for the PONG/NACK back-channel.
- `src/MindControl/Reactor.cs` — the decision loop and the safety rules: any
  feed doubt (disconnect, gap, lag, fps collapse, silence) sends PANIC before
  anything else. `NACK(4)` (disarmed) is a normal state, not an error.

## Dev loop

```powershell
# terminal 1, in the spectral-sight repo:
python tools/replay.py <clip>.jsonl --from 260 --speed 4

# terminal 2, here:
etc/dev.ps1                 # dry run: intents logged, no hardware
etc/dev.ps1 -Port COM5      # drive the real board
```

`dotnet test` needs nothing running.

Add `--trace data/ghost-trace.jsonl --self <champion>` to a dry run and open
`etc/ghost-viewer.html` (self-contained, drag the timeline + trace onto it) to
watch the ghost's cursor over the map, with every glance labeled with its
reason and jumpable from the tick strip.

Before a hardware run, `etc/minimap-calibrator.html` turns a screenshot of the
player's screen into the exact `--screen`/`--minimap` arguments: paste the
screenshot (Ctrl+V), click the minimap's two corners, copy the line.
