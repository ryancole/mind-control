# mind-control

An observe-and-advise coaching reactor: it consumes the live game-state feed
published by [spectral-sight](../spectral-sight) and prints real-time,
fair-play coaching feedback — which button a good player would have pressed,
which way they would have stepped, where they would have walked to, where a
new level's point goes, and why.
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
`spectral-sight/docs/output-format.md` (schema 2; schema 1 still accepted).

## Layout

Three layers; the policy is the one that churns and stays pure of I/O of its
own:

- `src/MindControl/Feed` — SSE → typed envelopes and events. Frames land in a
  latest-wins mailbox (capacity 1, drop-oldest: stale game state is never
  queued); events, gaps, and connection changes in an ordered notice queue.
- `src/MindControl/Policy` — `(state, event) → a coaching cue`. `JevPolicy`
  is the coach: it keeps the perception (who the player is, what has been
  seen for how long, which buttons the HUD has shown come back) and the
  geometry (the two sides of a bolt's line, which way is toward home, where on
  the map the player stands and how far each lane is), builds a
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

The `health:` line every five seconds ends with what the coach did in that
stretch: `coach: player Twitch; asked 18 (carry_on 17, buy 1)`. A silent
coach is then one of three things: `no player row` (it could not find the
player, so nothing was asked), `nothing to offer`, or answers that were all
`carry_on` or `(weak)`, below `DecideAt` — the rubric's call, which
`--audit` shows the state behind.

The execution-coaching fixture is `data/coach-full-20260902-222718.jsonl`
(local, gitignored): the whole of `Recording 2026-08-30 200315` exported with
`--coach` by spectral-sight's gated build of 2026-09-02, video 142–1121s. To
replay it:

```powershell
# terminal 1, in the spectral-sight repo (lane until ~700s, fights after):
python tools/replay.py ../mind-control/data/coach-full-20260902-222718.jsonl --from 140 --speed 4

# terminal 2, here:
etc/dev.ps1   # audit on by default: data/audits/audit-<stamp>.jsonl
```

Keep the replay speed modest: the coach asks its root question about the
moment up to four times a video-second, one in flight at a time, and one
question per event, and Jev's limit is 1,200 requests a minute. With one in
flight, the root runs at most as fast as Jev answers (about six a wall-second
at its median latency), whatever the speed.

While it runs it also serves the coaching feedback as SSE at
`http://localhost:8724/stream` (`--serve <port>` to move it, `--serve 0` to
turn it off). To watch it in a browser, open the brain view at
`http://localhost:8724/` (below), which reads the same lines. Every line
that is advice carries `model`, the Jev release whose answer it is, so a
client can tell the model's answers apart; status and roster lines are the
code's own and carry none. A `game` line — `{"t":"game","game":1,"video_time":…}` — says a
new match began in the same run (a VOD holding several, or a live run left up
across a queue): the coach has forgotten the last one, its rosters leave the
replay, and a panel clears its header. An `asking` line — `{"t":"asking","occasions":["decide","bolt"]}` —
says which questions are on their way to Jev right now, oldest first, and
`occasions` is empty once they are all back; it tracks the network, not the
coaching, so a panel can light a "thinking" indicator off it. It changes
several times a second, so it stays out of the stream's replay, and a newly
connected client gets only the latest one. The stream is output-only, like the console.

## The brain view

Open `http://localhost:8724/` while a run is going to watch the coach think.
It draws the tree of questions below as it is walked, a quarter video-second
at a time:

- **Senses**: the state Jev was shown (the `Moment`): health and mana, the
  buttons and which are up, where the player stands, the enemies on the
  screen, the minions, the team, and what the coach did lately. A dot flashes
  when a reading changes.
- **Cortex**: the root, `decide`, wired to all of its branches. A branch the
  moment did not offer is dark, with what closed it written under it (`no
  enemy on the screen`, `stepped 0.3s ago; a step a second`); an offered one
  is lit, with Jev's probability as a bar against `DecideAt`, and its
  follow-up's options beside it. While a question is on the wire the root
  pulses and the branches it offered shimmer; when the answer lands, a spark
  runs to the pick and, if it came to a deed, on to the hand that did it
  (keyboard, a click on an enemy, a click on the ground, an attack-move, or
  a cue). A bolt and a shot are the reflexes underneath, with their yes/no
  answers against `YesAt`. A `carry_on` pick is no order and is not drawn:
  the view stays on the last answer that picked something else.
- **Hands**: every key, step and cue, with the frames it recorded; tick
  *hesitations* to see the picks that fell below `DecideAt` too.
- **Brainwaves**: every root answer over the last minute of video, one
  column per answer and one row per branch, bright by probability, gold for
  the pick and green for a deed. Hover to look back at a moment, click to pin
  it.

It reads `/brain`, which is everything `/stream` carries plus a `thought`
line per question (`Thought` in `Policy/Thought.cs`): once when it is sent,
with the branches considered, the questions and the state, and once when it
is answered, with the answers, the verdict (`acted`, `carry_on`, `weak`,
`nothing`, `no answer`, `stale`) and what the hands did. A root with nothing to
offer is sent once as `idle`. Thoughts stay off `/stream`: they are bulky (a
state and its answers per question, several a second), and `/stream` stays
the lean advice-and-status stream for anything else that subscribes. The page
is one self-contained file, `src/MindControl/Brain/brain.html`, embedded in the build; open it as
`/?demo` to see it run on made-up thoughts with nothing else running.

## Coaching by Jev

The coaching is a tree of questions with one root. Every quarter of a
video-second (`AskEverySeconds`), one question in flight at a time, the
coach is asked *what would a good player do right now?* — a choice among
the things the moment makes possible:

| Option | Offered when | A pick is |
|---|---|---|
| `level_up` | the HUD shows a skill point waiting (alive or dead) | the level-up chord, Ctrl and the slot |
| `run_away` | an enemy champion is on the screen | said, not yet done |
| `use_ability` | a button the HUD has shown come back is up, with an enemy on the screen | a key press |
| `attack` | an enemy minion or champion is within (or a step past) basic-attack range | a right-click on it |
| `step_back` | in a lane with an enemy minion near | a sidestep back down the lane |
| `go_to_turret` | an enemy champion is on the screen, out of the base | said, not yet done |
| `hide_in_brush` | a patch of brush is near and they stand in none | a step into it |
| `catch_wave` | an enemy wave is at one of their turrets, away from them | an attack-move step toward it |
| `walk_to_lane` | the game clock is running | an attack-move step toward the lane's wave |
| `recall` | out of the base, with the clock running | said, not yet done |
| `buy` | in the fountain | said, not yet done |
| `carry_on` | always, first | no order |

Each option carries its own rubric (the `*Option` texts in
`CoachQuestions`), and the root's rubric says which wins where more than one
is right, in the order of the table. The follow-ups a branch needs — *which
ability?*, *which target?*, *which lane?*, *which lane's wave?*, *which
brush?*, *which ability takes the point?* — go in the same request, answered
whatever the root picks and read only for the branch it picked, since Jev
answers every question on its own and a second round trip would cost a
tenth of a second. So the coach does one thing at a time: a walk is never
undone by an attack ordered the same second. A pick below `DecideAt` (0.4)
is no order.

```
key[p2]: coach would have pressed Q here: Karma has been in Q range (980 units) for 2.0s with Q up  recorded: KeyDown Q (0x14), KeyUp Q (0x14)
step[p2]: coach would have attack-moved right toward bot lane, at your outer turret here: you are in your fountain at 0:50; a good player would be on the way up bot lane (bot lane, at your outer turret, 12086 units right)  recorded: MouseMove 1159,516, KeyDown A (0x04), KeyUp A (0x04), MouseButtons Left, MouseButtons None
key[p2]: coach would have pressed Ctrl+Q here: you reached level 7 at 5:12; a good player would put the point in Q (Mystic Shot: a skillshot poke)  recorded: KeyDown Ctrl (0xE0), KeyDown Q (0x14), KeyUp Q (0x14), KeyUp Ctrl (0xE0)
cue[p2]: coach would have recalled here: you are in bot lane, between your inner and outer turrets at 5:00; 20% health; no enemy on the screen
```

Movement is paced like a player's: a good player gets around in short
right-clicks on the ground, a fresh one about every second, so for
`MoveEverySeconds` (1) after a step (a dodge included) no other step is
offered, and then one is again whether the player stands or walks — a step
still being walked is no reason to hold the next. A walk's click lands 300px
from the player's model at 1080p, or on the spot itself once it is nearer, in
the game window, never on the minimap. An attack is not offered again for
`AttackEverySeconds` (1), since one right-click keeps a champion attacking;
and what the ghost's hands cannot yet do is not said again for
`SayEverySeconds` (5). A walk to lane goes where the lane's waves meet when
the minimap shows both, to the enemy's front when it is alone at one of the
player's turrets, and otherwise to where the lane is played. Whether standing
somewhere is idling (in the fountain after the clock has started, yes; in lane
waiting for minions, no; left at the turret while their own wave has pushed
out beyond it, yes) is the rubric's call, not a threshold in code. The
walks need a world-calibrated feed, since the map is read in game units.

A skill point (`skill_point` events, off the level-up chevrons the HUD draws
above Q/W/E/R) is noted and offered as `level_up` while it waits, with
*which ability takes it?* a choice among exactly the buttons the HUD lights —
the game's own answer to which abilities can take a point, so the ultimate is
offered at 6, 11 and 16 and a full ability never — each described by what the
ability is, its place in the champion's usual skill order when `AbilityKits`
has one, whether it has been seen cast this game, and how many points the
coach has put in it since it began watching. Holding the first point of a
game, against an invade, is the rubric's call: the root is asked again every
quarter second with how long the point has waited. The coach's own placement
is counted only when the HUD shows the point gone in (`skill_spent`, whose
`held_for` is logged as a cue); a point the player spends themselves is in
nobody's count, and an answer that arrives after the player has already spent
the point is dropped. The reader is off while the player is dead, so a point
spent from the death screen is reported on respawn.

Two events are asked about as they come, on top of the root:

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
player's own team; where the player stands on the map and how long they have
stood there, with each lane's distance, direction and allies (`RiftMap`, the
lanes as the lines their turrets lie on -- geometry, not a gate); what the
coach itself did in the last few seconds; and the event in question, with its
measurements; and a skill point waiting, when one is (the level, the buttons
the HUD lights for it, whether the ultimate is among them, and how long it has
waited). Everything is a measurement the
code made — the model is asked for judgement, never for arithmetic — and the
fair-play boundary is that this state is built from visible rows only.

The rubrics are the text in `CoachQuestions`; the thresholds that used to be
code (how long an enemy sits in range before a throw, how many wide shots
make a run) are sentences there now. The knobs that remain are plumbing:
`DecideAt`, the probability below which the root's pick is no order (0.4,
uncalibrated); `YesAt`, the same for the events' yes/no questions (0.6 — a yes
with a margin); `AskEverySeconds`, the floor between root questions (0.25);
`MoveEverySeconds`, `AttackEverySeconds` and `SayEverySeconds`, the pace of
steps, attacks and cues (1, 1 and 5); `StillRadiusUnits`, how far the minimap
read may jitter and still be the same spot (100); and `--model`, pinned to
`jev-1.13.0` because a threshold tuned against one release's calibration
should not move with `jev-latest`.

`--audit [file|none]` (on by default, to `data/audits/audit-<yyyyMMdd-HHmmss>.jsonl`) records every question and its answer as JSONL: the state
the model saw, the questions as asked, and the answers exactly as returned.
A press or a step in the log traces back to a probability there, and a
silence to the one that fell short; it is also the record of what the model
was shown, which is the fair-play boundary made inspectable. The model's
latency is about a tenth of a second, and the ghost runs that far behind the
moment: every output is stamped with the video time it was asked about, so
the trace and the log carry the coach's timing rather than the network's.

The audit also carries the farming's measure: a line per enemy minion that
died on the player's screen low enough to be a last hit, with `occasion`
`last_hit` or `missed_cs` and no questions, and a `why` that puts it down to
what the coach did in the 1.5 s before its bar was last seen: `coach
attacked`, `attack offered, picked <option>`, `attack closed: <gate>`, or `not
asked`. The health line every five seconds tallies the same (`farm: 2 taken, 3
missed (...)`, and `last hits N of M this game`), and every state carries it
as `farming`: the HUD's creep score, its rate from 1:30, and the last minute's
last hits taken and missed.

What the copy must not claim, and still does not: a bolt is "a bolt", never
an ability (a ranged auto-attack qualifies as readily as a skillshot, and
until spectral-sight names the ability nothing here tells them apart); a wide
shot is where the bolt passed, never "you missed"; an aim count is over the
shots that were seen, never over casts; the warning a bolt gave is stated,
never judged.

## Ghost input recording

The ghost's input -- the mouse and keyboard reactions the coach would have
made -- is also recorded to `data/msdr/ghost-<yyyyMMdd-HHmmss>.msdr`, a fresh file
per run named for the moment it started, in the wire format of the
[misdirection](../misdirection) HID bridge, via the
[misdirection-client](submodules/misdirection-client) library's protocol file
(`--record <file>` to name the file instead, which a run appends to, `--record none` to turn it off). Each run opens
with a `ScreenSize` frame; every key the coach presses follows as a `KeyDown`
and `KeyUp` pair (a level-up's point as the chord, `KeyDown Ctrl` before the
pair and `KeyUp Ctrl` after it, which the game reads as a point into the
ability rather than a cast), and every step as a
`MouseMove` to the ground 200px from the player's model in the step's
direction followed by a right button down and up — a move order, which is
how a step is taken in the game. The model's place on the screen is one
place, the camera being locked; `--anchor <x,y>` names it (default: the
screen's centre). A step toward somewhere farther — a lane, a wave, a
patch of brush — is a longer click aimed that way, 300px at a screen 1080
tall and scaled by height (400 units, more than a second's walk, so the
model does not stop between clicks), or on the place itself once it is
nearer than that: the coach walks in steps, one a second, and never clicks
the minimap. A walk's click never lands on ground no one can walk on — a
wall, or a turret, inhibitor or nexus, standing or fallen (`RiftWalls`, off
the map's navigation grid; `etc/navgrid-walls.py` regenerates it): the game
would stop the champion at its edge, so the click moves past it, up to 600px
at 1080 and above the HUD, and the game paths round it; the last leg, on the
place itself, moves short of it instead. A walk toward the fight — up a lane, to a wave — is ordered
as an attack-move instead: the same spot, but `KeyDown A`, `KeyUp A` and a
left button down and up in place of the right-click (the game's default
attack-move binding), so the champion stops to attack an enemy that comes
into range on the way. A step away from the fight — a run back, a step back,
a dodge, a step into brush — stays a plain move, which never stops to shoot.
It is a recording, not a connection: this tool never opens
the device. The file stays open for the run, shared for reading, and the
library's reader opens a file a writer still holds, so misdirection can play
the recording by path while a run is still appending to it: a read sees every
frame flushed so far and a clean end of file, never a torn frame.

The file carries the timing too, as the format's `FILE_DELAY` records: before
each press or step, the video time that passed since the previous one, so
`ProtocolFile.ReadTimed` (or misdirection's timed playback) replays the
ghost at the pace the coach acted. The clock is the VOD's, not the wall's — a
`replay.py` run at speed 4 records the same gaps as one at speed 1 — and it
starts at a run's first press or step, so a file holds the ghost's rhythm and
the trace below holds where in the video it began. A step is stamped at the
bolt's first sighting, which can be earlier than a press already written; the
file is one sequence and a gap cannot be negative, so such a step follows at
no gap and the clock does not move back.

What went into the file is also shown on the coaching line it came from,
after the advice and plainly: `recorded: Delay 3.400s, KeyDown Q (0x14), KeyUp
Q (0x14)` for a key (the gap before it, then the keycap and the HID usage on
the wire), `recorded: Delay 0.933s, MouseMove 819,399, MouseButtons Right,
MouseButtons None` for a step, and the `ScreenSize` frame on the startup line.
The same frames ride as data — `input`, a list of `{type, ...}` objects
(`delay`, `key_down`, `mouse_move`, `mouse_buttons`, ...) — on the SSE stream's
key and step lines and on the trace's. The text and the data carry no
decoration; the ghost viewer puts a ⌨ or 🖱 to a key or a step from the
type, which is its own choice of dress, and a coaching panel can do the
same. With `--record none` nothing is written, so nothing is shown. The trace
below is the record of *when* in absolute terms (a `key` or `step` line per
press or step, keyed by video_time, a step toward somewhere carrying its
`destination` on the map; a dodge is stamped at the bolt's first sighting, which is earlier
than the event that reports it, so the trace is not in time order there).

Add `--trace` (bare `--trace` writes `data/traces/trace-<stamp>.jsonl`) and open
`etc/ghost-viewer.html` (self-contained, drag the timeline + trace onto it) to
watch the coach's hands over the map: keys and steps sit on the tick strip as
⌨ and 🖱, jumpable, and while one is fresh a badge at the foot of the map
names it and the frames it recorded, and a step's destination is ringed on
the map with the way there from where the player stood.
