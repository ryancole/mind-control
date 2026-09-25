# mind-control

An observe-and-advise coaching reactor: it consumes the live game-state feed
published by [spectral-sight](../spectral-sight) and prints real-time,
fair-play coaching feedback — which button a good player would have pressed,
which way they would have stepped, and why.
It sends no input to the game or to any device; it only watches and explains.

The coaching itself is [Jev](https://docs.typesafe.ai/concepts/system-one)'s,
TypeSafe's System One model. Every decision is a question put to it about
the moment — *would a good player press Q now? was that bolt worth a step,
and which way? is this aim worth a word?* — and answered as a probability
or an option. This tool measures, asks, and turns the
answer into a demonstrated input; it decides nothing itself.

Two rules keep it fair:

- **Advises, never acts.** The output is coaching notes (and, optionally,
  the ghost's input as a protocol file, with a trace of when for the viewer).
  Nothing is ever sent back into the game.
- **Uses only what the player can see.** The model is shown enemies that are
  currently visible on the player's own screen, the player's own HUD, and
  allied deaths (which the game announces). It is never shown fog-of-war
  information — no enemy positions in fog, no "seconds since seen", no
  last-known spots, no level or cast sensed through the fog.

Input boundary: SSE feed at `http://127.0.0.1:8723`, wire format in
`spectral-sight/docs/output-format.md` (schema 1).

## Layout

Three layers; the policy is the one that churns and stays pure of I/O of its
own:

- `src/MindControl/Feed` — SSE → typed envelopes and events. Frames land in a
  latest-wins mailbox (capacity 1, drop-oldest: stale game state is never
  queued); events, gaps, and connection changes in an ordered notice queue.
- `src/MindControl/Policy` — `(state, event) → a coaching cue`. `JevPolicy`
  is the coach: it keeps the perception (who the player is, what has been
  seen for how long, which buttons the HUD has shown come back) and the
  geometry (the two sides of a bolt's line, which way is toward home), builds a
  `Moment` — the fair-play state the model is shown — and asks the questions
  in `CoachQuestions`. The only I/O is the injected Jev client, so a replayed
  timeline with a scripted client exercises it exactly (`dotnet test` needs
  nothing running and no key). `NoOpPolicy` watches and says nothing.
- `src/MindControl/Reactor.cs` — the decision loop and the safety rules: any
  feed doubt (disconnect, gap, lag, fps collapse, silence) pauses coaching
  rather than advising off stale state.

The Jev client is the [jev-dotnet](submodules/jev) submodule. The API key
lives in this project's user secrets, not the environment:

```powershell
dotnet user-secrets set Jev <key> --project src/MindControl
```

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
`--coach` by spectral-sight's gated build of 2026-09-02, video 142–1121s. To
replay it:

```powershell
# terminal 1, in the spectral-sight repo (lane until ~700s, fights after):
python tools/replay.py ../mind-control/data/coach-full-20260902-222718.jsonl --from 140 --speed 4

# terminal 2, here:
etc/dev.ps1 -- --self Ezreal --audit data/audit.jsonl
```

Keep the replay speed modest: the coach asks a question about the moment up
to four times a video-second and one per event, and Jev's limit is 1,200
requests a minute. A run at `--speed 4` sits around a third of that.

While it runs it also serves the coaching feedback as SSE at
`http://localhost:8724/stream` (`--serve <port>` to move it, `--serve 0` to
turn it off). The spectral-sight dashboard's COACHING panel subscribes to it:
open `http://127.0.0.1:8723/` and the cues appear next to the event log. Every line
that is advice carries `model`, the Jev release whose answer it is, and the
panel marks those with a ✦; status and roster lines are the code's own and
go unmarked. The stream is output-only, like the console.

## Coaching by Jev

Three questions, each asked when there is something to ask about:

- **Which button, now.** Whenever the player is alive, an enemy is on their
  screen, and a button the HUD has shown a cooldown for has counted down, the
  coach is asked one yes/no per such button: *would a good player press it
  right now?* A clear yes is a key press:

  ```
  key[p2]: coach would have pressed Q here: Karma has been in Q range (980 units) for 2.0s with Q up  recorded: KeyDown Q (0x14), KeyUp Q (0x14)
  ```

  Asked at most four times a video-second, one question in flight at a time.
- **A bolt at the player** (spectral-sight's `threat` events): *is this worth
  a remark?* and *would a good player have stepped?*, plus *which way?* as a
  choice between the two sides of the bolt's line, each described by whether
  it goes toward the player's own base and toward or away from the nearest
  visible enemy. A yes is a cue and a step, stamped at the bolt's first
  sighting:

  ```
  step[p3]: coach would have stepped up-left here: a bolt from the upper right hit you for 12 while you stood still, 0.33s after it came into view  recorded: MouseMove 819,399, MouseButtons Right, MouseButtons None
  ```
- **A shot of the player's** (`skillshot` events, only those seen leaving
  them with an enemy in front): *given the recent shots, is aim worth a word?*
  A yes is a cue naming where the bolt passed and the run it made.

What the model is told is the `Moment`: the player's champion, health, mana
and level; each button's status with what it is and how far it reaches
(`AbilityKits`, a fact table, not a gate); every visible enemy's distance,
screen direction, time in view and time inside the player's reach; the
player's own team; what the coach itself did in the last few seconds; and the
event in question, with its measurements. Everything is a measurement the
code made — the model is asked for judgement, never for arithmetic — and the
fair-play boundary is that this state is built from visible rows only.

The rubrics are the text in `CoachQuestions`; the thresholds that used to be
code (how long an enemy sits in range before a throw, how many wide shots
make a run) are sentences there now. The knobs that remain are plumbing: `YesAt`,
the probability below which a yes is a no (0.6 — a yes with a margin, and
since the coach is told what it just did, a press drops the next answer to
about 0.35, so a lower bar does not mean a spammed key); `AskEverySeconds`,
the floor between questions about the moment (0.25); and `--model`, pinned to
`jev-1.13.0` because a threshold tuned against one release's calibration
should not move with `jev-latest`.

`--audit <file>` records every question and its answer as JSONL: the state
the model saw, the questions as asked, and the answers exactly as returned.
A press or a step in the log traces back to a probability there, and a
silence to the one that fell short; it is also the record of what the model
was shown, which is the fair-play boundary made inspectable. The model's
latency is about a tenth of a second, and the ghost runs that far behind the
moment: every output is stamped with the video time it was asked about, so
the trace and the log carry the coach's timing rather than the network's.

What the copy must not claim, and still does not: a bolt is "a bolt", never
an ability (a ranged auto-attack qualifies as readily as a skillshot, and
until spectral-sight names the ability nothing here tells them apart); a wide
shot is where the bolt passed, never "you missed"; an aim count is over the
shots that were seen, never over casts; the warning a bolt gave is stated,
never judged.

## Ghost input recording

The ghost's input -- the mouse and keyboard reactions the coach would have
made -- is also appended to `data/ghost.msdr` in the wire format of the
[misdirection](../misdirection) HID bridge, via the
[misdirection-client](submodules/misdirection-client) library's protocol file
(`--record <file>` to move it, `--record none` to turn it off). Each run opens
with a `ScreenSize` frame; every key the coach presses follows as a `KeyDown`
and `KeyUp` pair, and every step as a
`MouseMove` to the ground 200px from the player's model in the step's
direction followed by a right button down and up — a move order, which is
how a step is taken in the game. The model's place on the screen is one
place, the camera being locked; `--anchor <x,y>` names it (default: the
screen's centre). It is a recording, not a connection: this tool never opens
the device.

What went into the file is also shown on the coaching line it came from,
after the advice and plainly: `recorded: KeyDown Q (0x14), KeyUp Q (0x14)`
for a key (the keycap and the HID usage on the wire), `recorded: MouseMove
819,399, MouseButtons Right, MouseButtons None` for a step, and the
`ScreenSize` frame on the startup line. The same frames ride as data —
`input`, a list of `{type, ...}` objects (`key_down`, `mouse_move`,
`mouse_buttons`, ...) — on the SSE stream's key and step lines and on the
trace's. The text and the data carry no
decoration; the ghost viewer puts a ⌨ or 🖱 to a key or a step from the
type, which is its own choice of dress, and a coaching panel can do the
same. With `--record none` nothing is written, so nothing is shown. The
format carries no timing, so the trace below is the record of *when* (a `key`
or `step` line per press or step; a step is stamped at the bolt's first
sighting, which is earlier than the event that reports it, so the trace is
not in time order there).

Add `--trace data/ghost-trace.jsonl --self <champion>` and open
`etc/ghost-viewer.html` (self-contained, drag the timeline + trace onto it) to
watch the coach's hands over the map: keys and steps sit on the tick strip as
⌨ and 🖱, jumpable, and while one is fresh a badge at the foot of the map
names it and the frames it recorded.
