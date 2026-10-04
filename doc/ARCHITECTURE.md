# ARCHITECTURE.md — Project CIPHER

Describes how the simulation core and the presentation layer fit together and why
the command/event split exists. Updated at stage 2.

## Layering

```
                              ┌───────────────────────────────────────────┐
                              │           PRESENTATION (Unity)            │
                              │                                           │
                              │   Views, UI, Input, Audio, Localization   │
                              │                                           │
                              │   • reads WorldState (read-only)           │
                              │   • builds ICommand values                 │
                              │   • subscribes to GameEvent stream        │
                              │   • maps ReasonCode → localized text       │
                              └───────┬───────────────────────────┬───────┘
                                      │ Execute(ICommand)        │ Subscribe(...)
                                      │ reads                    │ events
                                      v                           ^
  ══════════════════════════════════ │ ══════════════════════════ │ ═══════════
                        HARD BOUNDARY — the dependency arrow points
                        one way only. Core never references Unity.
  ══════════════════════════════════ │ ══════════════════════════ │ ═══════════
                                      v                           │
                              ┌───────────────────────────────────┼───────┐
                              │        PROJECTSPY.CORE            │       │
                              │            (netstandard2.1)      │       │
                              │                                   │       │
                              │   GameSession                     │       │
                              │     ├── Execute(cmd) ─────┐      │       │
                              │     ├── AdvanceTick()     │      │       │
                              │     ├── AdvanceTacticalStep()     │       │
                              │     ├── SessionMode (Strategic│     │       │
                              │     │                 /Tactical)  │       │
                              │     └── CommandLog        │      │       │
                              │                           v      │       │
                              │   ICommand ──> Validate ──> CommandResult   │
                              │              Apply                     │       │
                              │                                   │       │
                              │   TickPipeline (canonical order)   │       │
                              │     RoomConstruction                │       │
                              │     Training                        │       │
                              │     Recovery                        │       │
                              │     MissionProgress                 │       │
                              │     EventChecks                     │       │
                              │     StatusDecay                     │       │
                              │                                   │       │
                              │   TacticalClock (Stage 4c pipeline) │       │
                              │     ApplyQueuedCommands             │       │
                              │     MovementResolution              │       │
                              │     ActionProgress                  │       │
                              │     NoisePropagation               │       │
                              │     Perception                     │       │
                              │     NpcPlanning                    │       │
                              │     AlarmUpdate                    │       │
                              │     DamageAndStatus                │       │
                              │     ObjectiveCheck                 │       │
                              │     EventEmit                      │       │
                              │                                   │       │
                              │   Fixed32 — int cm, no float in    │       │
                              │   any rule path (rule 6)            │       │
                              │                                   v       │
                              │   GameEvent ───────────────────────┘       │
                              │                                           │
                              │   WorldState (serializable root)           │
                              │     Seed · Clock · Resources              │
                              │     BaseLayout · Agents · Recruits        │
                              │     Missions · Contracts · Flags          │
                              │     RngStreams (9 named streams)           │
                              │     ActiveMission · SleeperOperations     │
                              │     IntelSnapshots                        │
                              │                                           │
                              │   RoomDefinitions ──> Tables (room_type,   │
                              │                       room_upgrade)       │
                              └───────────────────────────────────────────┘

                              ┌───────────────────────────────────────────┐
                              │  PROJECTSPY.TABLES  (netstandard2.1)     │
                              │  Luban-generated beans + binary data     │
                              │  Referenced by Core for all balance      │
                              │  numbers. Read-only content.             │
                              └───────────────────────────────────────────┘

                              ┌───────────────────────────────────────────┐
                              │  PROJECTSPY.SIM  (net8.0 console)        │
                              │  Headless play / sim / verify            │
                              │  Same Core, no Unity — stage 6 onward    │
                              └───────────────────────────────────────────┘
```

Three assemblies load the same `ProjectSpy.Core.dll`: Unity, the console simulator
and the test runner. That is the reason Core targets `netstandard2.1` and carries no
third-party dependencies — a behaviour difference between the simulator and the game
would make the stage-6 balance harness worthless.

## Command flow

Presentation never mutates the world. It describes an intent, Core decides whether
that intent is legal and what it does.

```
   Player clicks "Build"
            │
            v
   new BuildRoomCommand(typeId, x, y, width, nameKey, cost)
            │
            v
   ┌──────────────────────────────┐
   │ GameSession.Execute(command)  │
   └──────────────────────────────┘
            │
            v
   command.Validate(world) ──► CommandResult
            │                        │
      side-effect free       ┌───────┴────────┐
                            │                │
                          Ok              Rejected(reason, args)
                            │                │
                            v                v
                    command.Apply(world,   state untouched;
                     world.RngStreams[...]) publish CommandRejected
                            │
                            v
                    append to CommandLog
                            │
                            v
                    queue GameEvents ──► flush ──► subscribers
```

Three properties this buys:

1. **Every mutation is logged.** Because `Execute` is the only path that changes
   state, `CommandLog` plus `Seed` fully describes the run. That is what stage 5's
   `ReplayVerifier` re-executes to prove determinism.
2. **Rejections are safe by construction.** A rejected command changes nothing, so
   the UI can call `Validate` freely for ghost previews and button gating without
   side effects.
3. **Core never writes prose.** `CommandResult` carries a `CommandReason` enum and a
   `CommandArgs` struct; Presentation looks up `command.reject.<reason>` in its
   localization table and substitutes the args. Thai is the default language.

`GameSession.Execute` re-runs `Validate` a second time before applying and throws if
the result disagrees. A command that passes once and rejects on the second call is
non-deterministic, which would corrupt the log — that is a bug worth crashing on
rather than papering over.

## Event flow

Commands and tick phases never publish directly to subscribers. They queue into a
pending buffer that `FlushEvents` drains in order:

```
   Execute / AdvanceTick
            │
            v
   _pending.Add(new RoomBuilt(...))        ← buffered
   _pending.Add(new ResourcesChanged(...))
            │
            v
   FlushEvents() → snapshot handlers → dispatch in order
```

Without the buffer, a UI handler could re-enter `Execute` mid-command and observe a
half-mutated world — the classic Presentation bug that is miserable to reproduce
because it depends on click timing.

Events are immutable records of facts, published *after* the change they describe.
Presentation renders from events; it does not poll Core state on a frame loop to
work out what happened.

## Two time scales

There are two clocks, and they are never live at the same time.

```
   STRATEGIC                                  TACTICAL
   ─────────                                  ────────
   StrategicClock                             TacticalClock
   1 tick = 1 in-game hour                    1 step = 100 ms simulated
   24 ticks = 1 day                           10 steps = 1 simulated second
   168 ticks = 1 week                         36000 steps = 1 strategic tick
   speeds: Pause 1x 4x 16x 64x                speeds: Pause 0.5x 1x 2x 4x
        │                                           │
        │            ┌──────────────────┐            │
        └───────────▶│  SessionMode     │◀───────────┘
                     │  Strategic       │
                     │   or Tactical    │   exactly one, never both
                     └──────────────────┘
                                  │
                     mission ends │ ticks = ceil(steps / 36000)
                                  │ applied EXACTLY ONCE
                                  ▼
                            back to Strategic
```

**The base clock freezes during a mission.** This is enforced by a throw, not a
no-op: `GameSession.AdvanceTick` (and `AdvanceStrategicTick`, `AdvanceTicks`,
`AdvanceToNextDay`, `AdvanceToNextWeek`) all throw `InvalidOperationException` while
`SessionMode` is `Tactical`. A silent no-op would let a system call it forever and
never find out; the throw names rule 13 and points at `AdvanceTacticalStep`, so the
failing call site says what to do instead.

A speed setting only maps real seconds to ticks or steps. It never changes what a
tick or a step computes — `ChangingSpeedDoesNotChangeWhatAStepDoes` asserts that two
sessions at different speeds which simulate the same number of steps reach the same
state hash.

`MissionTimeConverter.StepsToStrategicTicks` rounds **up**, so any elapsed mission
time costs at least one tick; rounding down would make the first 59 minutes of every
mission free. `TacticalState.TimeConverted` makes the conversion idempotent, so a
quit-then-load cannot silently eat an extra hour of base time.

Input is sampled only at step boundaries and logged as `ICommand`s. Rendering
interpolates between steps; the simulation never does.

## Time and the tick pipeline

`StrategicClock` owns a single `Tick` (a `long`, hourly granularity: 24 ticks per day,
168 per week). It raises `OnTick`, `OnDayChanged` and `OnWeekChanged`, and knows
nothing about what a tick *means* for the world.

The per-tick work is `TickPipeline`, which runs six phases in a fixed order:

```
   RoomConstruction → Training → Recovery → MissionProgress → EventChecks → StatusDecay
```

This order is part of the save contract. Reordering it changes every outcome for an
existing save and invalidates every recorded replay, so it is declared once in
`TickPipeline.CanonicalOrder` and asserted by `TickOrderTests`. Phases are stored in a
dictionary and executed in enum order, so a phase registered later cannot accidentally
run in the wrong slot.

The tactical step pipeline is a **second, separate** order, also part of the save and
replay contract (knowledge.md rule 6). It runs only inside a mission, and the two
pipelines never interleave:

```
   ApplyQueuedCommands → MovementResolution → ActionProgress → NoisePropagation
   → Perception → NpcPlanning → AlarmUpdate → DamageAndStatus → ObjectiveCheck
   → EventEmit
```

That order landed with stage 4c and is asserted by `TacticalStepOrderTests`, which
checks the sequence against `TacticalStepPipeline.CanonicalOrder` and checks that it
does not depend on registration order — a phase registered later must not be able to
run in the wrong slot, exactly as `TickOrderTests` requires of the strategic pipeline.

`GameSession.AdvanceTacticalStep` runs the whole thing. It is the only caller, which is
the point: a mission has one entry point for "advance time", so the canonical order
cannot be honoured by some callers and skipped by others.

The order is not arbitrary; each dependency has a consequence if it is violated:

| Constraint | What breaks if reversed |
|---|---|
| `RoomConstruction` before `Training` | A room completing construction this tick cannot be used until next tick. Every new room silently costs the player a tick. |
| `Training` before `Recovery` | Training would never actually cost anything: the stamina the phase consumes is topped up in the same tick. |
| `Recovery` before `MissionProgress` | Agents deploy on the stamina they finished the previous day with rather than today's. |
| `EventChecks` after `MissionProgress` | A mission that finished this tick is not yet resolved when events roll, so it affects the wrong tick's rolls. |
| `StatusDecay` last | Drift, morale and burnout would be computed against a half-updated world. |

`TickOrderTests` asserts the sequence, asserts that it is independent of registration
order, and asserts each of these pairwise relationships individually — so a future
reorder fails as a named constraint rather than as an unexplained simulation change.

`TacticalStepOrderTests` does the same for the tactical pipeline.

### Phase implementations

All six strategic phases are implemented as of stage 3 (`Phases.CreateDefault`), and all
ten tactical phases as of stage 4c (`TacticalPhases.CreateDefault`).

`MissionProgress` is deliberately thin: it advances the mission list and applies the
mole leak roll, but no missions are generated yet. `MoleSystem.AccumulatedLeakDifficulty`
is the seam stage 4 reads — a new mission's difficulty starts from that total, so a mole
who has been leaking for three weeks makes the agency measurably worse at everything
without the player ever being told which agent is responsible.

`EventChecks` also carries the weekly work — settlement, the mole's Heat pass, contract
refresh, and the counter-intel step budget — guarded by `WorldState.LastSettledWeek` so
that advancing a large batch of ticks at once still settles each week exactly once
rather than once per tick spent on the settlement day.

## No coordinates in Core

Core stores no Unity types, no render types, no meshes, no animation data and no
screen or camera data (knowledge.md rule 10). This is not only a Unity-boundary
concern: a position mutated by anything other than a command is not replayable, so any
coordinate in Core is determinism debt.

### The one exception: tactical space

knowledge.md rule 10 permits **logical** positions in tactical space, scoped to
`Core.Tactical`:

```
   TacticalPosition = { FloorIndex: int, X: Fixed32 }   // 1 unit = 1 cm
```

The justification is the same one that bans them everywhere else: every position
change is either a logged `ICommand` or the output of a deterministic step, so the
seed plus the command log fully describes the position history. `Fixed32` is an
`int`-backed centimetre count, so vision, noise, reach and light are integer interval
maths — never float.

What the exception does **not** cover: Unity types, meshes, camera or screen data, and
the base layout. The base stays `SlotIndex` / `Layer` / `SlotCount`, as below.

### The tactical simulation

Stage 4c put the whole of a mission in Core. `TacticalState` owns the building's
occupants — the squad, the generated guards and civilians — and every step is resolved
by the ten-phase pipeline above.

Three properties are load-bearing and each is asserted rather than assumed.

**Core is the sole authority on perception (rule 12).** `PerceptionSystem.CanPerceive`
answers one question, can this observer make out this target, and returns a `Perception`
carrying the full breakdown: base range, range bonus, cone, occluders, light, posture,
movement and disguise, each as its own `PerceptionTerm` with a localization key. The UI
renders that breakdown; it does not recompute it. The gate order is cone, then occluders,
then light, then range, so the reason a target was not seen is the *first* reason and not
whichever one happened to be cheapest to test. Dark is zero range, not a reduced range:
there is nothing to see in an unlit room, and a system that let a guard notice you at
arm's length in the dark would make every other thing the perception system says
unbelievable.

**Perception is not symmetric.** Only a guard perceiving an agent produces suspicion.
Guards do not look for each other, and the squad does not grow suspicion of the building
it is standing in. The alarm is likewise computed from guards alone — it is the *site's*
awareness, and counting the infiltrators among the things the site is aware of would let
a mission raise its own alarm with nobody having noticed anything.

**Noise is a graph walk, not a straight line.** A noise is a radius on one floor.
Crossing a connection shrinks it by `noise_profile.attenuation_per_connection_percent`
and crossing a floor shrinks it further by `attenuation_per_floor_percent`; what
survives is spent covering the listener's own room, measured from where the sound came
in — the noise's own position, or the door it arrived through. A straight-line test would
be wrong the moment a wall is between them, and this is a building. Whoever made a noise
is not one of its listeners. Every noise reaches the mission log naming its source and
its listeners, because "noise is the primary way a careless player loses, so it must be
legible".

### Deployment

A mission starts by putting a squad into an entrance room and the generated guards into
their patrol routes' first rooms. Both used to mean "the middle of the room", which meant
a routine site could begin with two guards' faces 30cm from the whole squad — a
confirmed identification on step one, and a burned site inside a minute with the player
watching. Two rules now hold:

- `TacticalMission.StartPlacement` hands out slots from the middle outwards, so no two
  actors deployed into one room begin on the same centimetre.
- The generator does not home a guard in the entrance room at all. That is a structural
  property of the building rather than a tuning number, so no CSV column owns it.

`TacticalDeploymentTests` pins both, and adds that a mission nobody has touched stays
`Calm`: a mission the player can lose on their own terms.

### Invariants

`TacticalState.ValidateActors` is the mission's own contract — every actor has a valid
position inside some room, health within its bounds, a downed actor with a bleed-out
timer. `TacticalFuzzSweepTests` runs **5,000 headless missions** across every site
template under four scripted policies (patient, reckless, passive, and one that walks
at the nearest guard and hits it, lethal and otherwise) and asserts the invariants on
every one, printing the outcome, alarm-band and policy distributions as it goes. The
aggressive policy exists because movement-only policies report a total of zero lethal
acts across five thousand missions, which is a true number about a game nobody played.

The sweep is what finds the bugs nobody thought to write a case for — movement that
added its distance to `X` regardless of direction, a clamp to a room's exclusive right
edge, and damage subtracted from a corpse before the damage system was asked whether it
could land. `TacticalMovementAndDamageTests` holds the fast versions so a regression does
not take six minutes to notice.

`x`, `position` and `width` are rejected by `CorePurityTests` everywhere on Core's
public surface **except** inside `Core.Tactical` and the site-layout types in
`Core.Missions` — see "Sites are intervals, not points" below for why the second
namespace needs the same exemption. The exemption is split by category rather than
granted wholesale: coordinate words are forgiven in those two namespaces, and render words
are forgiven nowhere, so `IntelSnapshot` being able to say `StartX` does not make `Mesh`
acceptable next to it. That is why `Fixed32`'s members are
named for what they are rather than for what they multiply — `PercentDivisor`, not
`PercentScale` — since a member called `Scale` reads as a render scale and the guard
that catches that would then have to be loosened instead.

### Mission interiors

`RoomContents` is the whole of a node's interior as far as the simulation is
concerned — a list of interactables, each with a stable id, a type, a state, and an
abstract `SlotIndex`. Presentation decides that slot 3 is two metres to the left in 3D,
the leftmost alcove in 2D, or nothing at all.

Walking around a room is presentation. What reaches Core is always an `ICommand`, which
is what keeps a traversal replayable.

### The base layout

The base was a `Width` x `Height` lattice with an occupancy array, and rooms knew
their cell. That put a 2D presentation decision inside the simulation. It is now:

| Was | Is |
|---|---|
| `GridX`, `GridY`, `Width` | `SlotIndices` (a set), `Layer` |
| adjacency derived by comparing coordinates | `AdjacentRoomIds`, explicit and symmetric |
| `RoomAt(x, y)` | `RoomInSlot(layer, slot)` |
| `RoomsAtDepth(y)` | `RoomsAtLayer(layer)` |
| `OccupiedCellCount` / `OccupancyPercent` | `OccupiedSlotCount` |
| `PlacementError.OutOfBounds` / `OverlapsExisting` / `NonPositiveWidth` | `LayerOutOfRange` / `SlotOccupied` / `NoSlots` |

Two things survived deliberately, because they are rules rather than rendering:

- **Layer.** Depth is a rule input: rooms in deeper layers cost more to build
  (`ApplyDepthCost`). Only the *interpretation* of a layer is presentational.
- **Merge-adjacency.** Two rooms can still be required to share a layer, which is what
  stops a room absorbing something placed directly above it.

### Two consequences worth stating plainly

**Adjacency is now data, so distance no longer implies it.** Two rooms can be
numerically side by side and not touch, because Presentation decides who touches whom.
Anything that wants grid behaviour has to declare it — which is exactly the work a
floorplan UI would do anyway.

**Placement is now a richer command.** `BuildRoomCommand` carries
`(TypeId, Layer, SlotIndices, AdjacentRoomIds, NameKey, TotalCost)` rather than a grid
position. `CorePurityTests` fails the build if a coordinate-shaped member reappears.

Test code that wants to think in grid terms goes through `GridCommands.Build` and
`World.Place`, which translate a grid triple into slots, a layer, and derived
adjacency — the same translation a real floorplan UI performs. That kept ~115 call
sites unchanged while Core stayed coordinate-free.

### Not yet done

`ICommand` still has no tick cost. The rule requires every rule-affecting action to
carry one, and today commands apply instantly; the first timed actions arrive with
stage 4's mission interiors.

## Sites are intervals, not points

`SiteGenerator` (`Core.Missions`) builds a mission site as a **continuous building**:
floors, each holding an ordered list of rooms occupying non-overlapping intervals
`[startX, endX)` along one line, joined by explicit connections. A vertical move happens
only through a connection that says it is vertical. This is rule 16 — a mission is a
building, not an abstract graph of abstract nodes — and it is what makes vision, noise
and reach interval arithmetic on a single fixed-point line rather than Euclidean distance
queries.

Everything is centimetres, in `Fixed32`, end to end. The tables speak lane units
(1 = 1 metre) because that is how a designer thinks about room widths; `SiteLayout` speaks
centimetres because that is what `Fixed32` stores. The conversion happens once, at
generation, and the ascii dump converts back at the edge for readability.

### The generation order is fixed, and each step is a pure function of the seed

```
floors and widths → partition each floor → horizontal connections
→ vertical connections → entrance, objective, alternate route, extraction
→ lights, occluders, interactables → guards and patrol routes → civilians
→ forward command post
```

The order matters because the later steps depend on the earlier ones being settled, and
because a different order would produce a different building from the same seed — which
would invalidate every recorded replay of every site. `SiteGenerator` calls
`SiteLayout.Validate` before returning, so a generator bug throws naming the site rather
than surfacing as an unwinnable building three screens later.

### Six invariants, asserted twice

Generation asserts these internally, and `SiteGeneratorTests` re-asserts them
**independently** — written out from the brief rather than calling `Validate`, because a
validator checked against itself only proves self-consistency:

1. every room is reachable from the entrance
2. at least two routes reach the objective that do not share every connection
3. every floor with a room has at least one vertical connection
4. no room is narrower than its template minimum; no overlapping intervals
5. every patrol route is traversable by its guard archetype
6. at least one extraction point is reachable from the objective

Twenty thousand sites across every template and tier, with the distributions printed rather
than asserted — floor counts, room counts, route counts and generation time are the
deliverable, and a silent distribution is a distribution nobody reads.

### A save is a seed

`SiteLayoutSaveData` is five numbers and a list of observed room ids. The building itself
is not stored, because `SiteGenerator.Generate` can rebuild it exactly from `MapSeed`. The
canonical dump in `SiteLayoutSerializer` exists to make that claim *checkable*: two layouts
that regenerate identically produce byte-identical text, and if they ever did not, the diff
would name the field that drifted.

The observation set is genuinely state — the difference between an empty room and an
unvisited one — and is stored sorted, so two saves of the same run are identical regardless
of which side of the building the team entered from.

### Room contents do not exist until observed

Knowledge.md rule 11. A room's loot roll and prop layout are generated on first
observation from `(MapSeed, roomId, "contents")`, never up front, so the order in which a
player explores can never change what any room contains. `SiteRoomDetailGenerator` is
`internal`, which makes `SiteLayout.ObserveRoom` the only door onto it, and
`SiteLazyContentTests` walks the compiled public surface to prove no other door exists.

## Pre-mission intelligence

Stage 4b is the boundary where truth becomes belief, and it is the only place in the
codebase that deliberately misreports something.

### The operation

A `SleeperOperation` advances on the **strategic** clock (rule 13), so it accrues while the
player builds, trains and waits — which is what makes it a cost rather than a menu click.
Each tick it accrues intel from `sleeper_op.csv` and rolls discovery:

```
chance = base_discovery_chance_per_tick
       + site_security_grade / intel_security_grade_divisor
       - (Infiltration / intel_infiltration_points_per_reduction) * infiltration_modifier
       - (Social       / intel_social_points_per_reduction)      * social_modifier
clamped to [intel_discovery_chance_min, intel_discovery_chance_max]
```

Subtraction, not division. A division-based reduction turns every skilled agent into a
zero-risk one the moment their skills clear a threshold, and the player would be told
"safe" for a site that would eat them alive. Accrual is the same story from the other side:
`IntelProgressHundredths` carries the remainder, because `100 / 24` truncating to 4 would
give every tier the same speed and make `ticks_per_intel_percent` a lie.

On discovery the operation raises Heat and resolves to `Burned` or — on the poison roll —
`Poisoned`, which keeps reporting. `Discovered` is the moment, carried on the event, not a
resting state: a tick is atomic and nothing can observe it in between.

### Five bands, each a strict widening

`intelPercent` maps to a band; the edges live in `intel_rule`, not Core, because they are
the numbers written out in knowledge.md rule 17 and a designer retuning the reveal curve
should be editing a spreadsheet. `IntelBandContents` describes each band as flat booleans
so the base screen can diff "what I have" against "what the next band buys" without
localizing a sentence in Core.

`IntelBandTests` asserts per band, not by count: a report that correctly withholds room
types while quietly including every patrol route has not leaked a count, and a count-based
check would pass it.

### Stale is a real state, and it is scattered

Once an operation has been embedded longer than `intel_stale_after_ticks`, a sampled
fraction of its facts are reported as `Stale` from `SleeperPrior`. Sampled **per fact**,
because a uniformly stale map and a uniformly current one are both useless to read: the
player cannot tell which parts to trust, so they trust none of it. Confidence and source
are one decision read twice — a fact is either the sleeper's current word for it or their
earlier one, and there is no third thing.

Nothing pre-mission is ever `Observed`. That is what the tactical layer writes, and a
snapshot claiming otherwise would be claiming the team has already been inside the
building.

### Poison is bounded, and the bound is structural

A poisoned operation lies in three specific ways: the objective is in the wrong room, some
doors report the wrong lock state, and some patrol routes are invented. It never adds or
removes a connection — a building the player cannot walk is not a hard mission, it is a
broken one, and it looks like the game cheating.

`PoisonPlan` settles the objective claim first, walks the **real** route from the entrance
to whichever room it now claims, and refuses to lie about any door on it. So the most
direct way to the room the report points at is described exactly as it is; if a door there
is locked, the report says locked and the player can see they have a problem.

That is deliberately weaker than "an unlocked route always exists", which would be nicer to
claim and is not true — the generator guarantees an alternate *route*, not an alternate
*unlocked* one. Because the guarantee is structural rather than statistical, the test can
sweep two thousand poisoned reports and assert on every one of them, and it cannot be
flaky.

### The tactical layer may contradict the report, and says so

`MissionFog` is derived from the snapshot at mission start: rooms and connections the
report named are `Reported`, everything else is `Unknown`. Promotion is the only mutator
and it refuses to move anything down, because a map that un-revealed itself as the team
walked would flicker.

When the team sees something the report said differently, the room is promoted to
`Observed` — the observation is true, the claim was not — and `IntelContradicted` is
raised. knowledge.md rule 17 is explicit that stale intel must never be silently
corrected, and a report that could never be caught being wrong could never have been worth
anything.

### Rescue

A `CaptureRecord` holds an agent, the facility holding them, and a countdown from
`capture_site.rescue_time_limit_days`. The countdown runs on the strategic clock *while a
rescue mission searches the building*, which is why the number is visible before the
mission rather than after it. At 100% intel the report names the room; below 100% the team
knows the prisoner is in the building and nothing else, so a rescue is a search.

### The UI contract is data

`SleeperOperationView` hands the base screen everything it needs — intel percent, band,
elapsed and estimated remaining ticks, per-tick discovery risk, the next band and what it
adds, whether recall is available, what a discovery would cost in Heat. No strings; the
screen builds its own text. If the screen were allowed to recompute the discovery risk in
C# there would be two copies of the formula and they would drift.

## Determinism

Three mechanisms, in order of how much they matter:

1. **Named RNG streams.** `RngStreams` derives nine independent generators from
   the world seed — World, Mission, Event, Recruit, Trait, Tactical, Goap, Sleeper,
   Loot. Without this, one extra roll in trait generation would shift every mission map
   afterwards, invalidating saved replays and making bugs nearly impossible to bisect.
   Each command receives the stream it needs; none shares a global generator. Sleeper
   and Loot exist because stage 4 added the two systems that would otherwise have
   borrowed Mission and Tactical, and borrowing is how a save stops reproducing the
   moment somebody tunes an unrelated number.
2. **Integer arithmetic in rule paths.** `float`/`double` rounding differences
   between platforms are determinism bugs, not trivia. `XorShift128Rng` uses only
   exact integer ops, and `BaseLayout.ApplyDepthCost` rounds with integer division.
3. **No ambient state.** Core never touches `DateTime`, `System.Random`,
   `Guid.NewGuid`, `File`, `Directory` or `Environment`. `CorePurityTests` reflects
   over the compiled assembly and fails if any such type appears on Core's public
   surface or in its reference table. Stage 5 adds the full grep audit and a Roslyn
   analyzer.
4. **No coordinates.** Also enforced by `CorePurityTests`, by member name. A field
   called `GridX` passes every type-level check — the type is an innocent `int` — so
   the rule has to be enforced on names as well as types. See "No coordinates in
   Core" above.

`WorldState.ComputeStateHash()` produces a stable, integer-only fingerprint
including RNG state — two worlds that look identical but will roll differently are
not the same world, and the hash must say so.

### The replay input

`GameSession` records an ordered log of commands and tick advances
(`ReplayEntry`), and `Replay` re-executes it in order.

Stage 1 kept two parallel logs — a command list and a tick-batch list — and replayed
every command before the first tick batch. That works only while the player issues
commands exclusively before the first tick. As soon as they do not — build a room,
play a week, build another — both builds replay before the week ever runs, and the
final state differs for reasons that have nothing to do with determinism. A determinism
test built on that arrangement would have proved nothing, because the divergence would
have been in the harness. The order is now recorded directly rather than reconstructed.

Tick advances are recorded as a single batch entry rather than N singles, because the
batching decides whether a periodic rule (weekly settlement, pool refresh) fires. It
has to be replayed exactly rather than inferred.

### The two determinism tests

`DeterminismTests` carries the two headline guarantees, plus the counterweights that
stop them passing vacuously:

| Test | Guards |
|---|---|
| `AYearOfUnattendedSimulationCompletesWithoutThrowing` | no state combination throws |
| `AYearOfSimulationNeverProducesANegativeResource` | checked every day, not just at the end |
| `AYearOfSimulationLeavesNoAgentInAStuckStatus` | room/status referential integrity |
| `TwoSessionsWithTheSameSeedAndCommandLogAreByteIdentical` | the byte-level guarantee |
| `ReplayingARunIntoAFreshSessionIsByteIdentical` | the replay path itself |
| `DifferentSeedsActuallyDiverge` | that the seed is *used* |
| `ADifferentCommandLogProducesADifferentWorld` | that commands matter |
| `TheYearRunActuallyDoesSomething` | that the run is not trivially stable |

The last three exist because a determinism test passes just as happily against a
simulation that ignores its seed, ignores its commands, or does nothing at all.

`WorldStateSerializer.ToCanonicalBytes` produces the byte comparison. `VerifyReplay`
compares `ComputeStateHash`, which is a weaker check: two different worlds can hash
alike if the hash does not cover every field, so the serializer is the authority and
the hash is the cheap check.

## Where balance numbers come from

Since stage 2 no tuneable value is hard-coded in Core. Everything lives in
`data/*.csv`, is compiled by Luban into `ProjectSpy.Tables`, and is read back through
two accessors: `RoomDefinitions` for rooms, and `SimulationRules` for everything stage 3
added.

`SimulationRules` exists so that rule systems ask for a *name* rather than a column
index, so there is one place to look when a designer asks "where does 33 come from",
and so tests can assert a rule is genuinely backed by table data. Every accessor
degrades to a documented default when the tables are absent — `AreTablesLoaded` lets a
test tell "the rule returned its fallback" from "the rule returned real data", which
is what keeps the fallback honest.

Core references `ProjectSpy.Tables` directly. That is allowed and does not violate
the Unity boundary: the tables assembly is pure compiled data (beans and enums),
contains no Unity types, and targets `netstandard2.1` like everything else.

```
  data/*.csv  ──pwsh tools/gen.ps1──>  src/ProjectSpy.Tables/Gen   (C# beans)
                                         assets/data/tables          (*.bytes)
                                                  │
                                                  v
  Core ── SimulationRules.RecoveryFor(4001).MentalRatioPercent ──> read at runtime
```

The accessor style is deliberately tolerant: an id that is missing from the tables
returns a documented default instead of throwing, because stage 5 deliberately
loads saves that may reference retired ids. Missing data is a build failure
(`TableValidator`), not a runtime crash.

### Table integrity is enforced, not hoped for

`TableValidator` runs as a test and checks more than "every id is unique". Several
rules exist because a table can be internally consistent and still make a system
silently do nothing:

- **`ValidateCounterIntelBalance`** — a case must be able to conclude (best case
  clears the threshold) *and* must not conclude on average (the expected case does not).
  The second half is as important as the first: evidence accrues identically whoever
  you investigate, so a threshold below the expected case would have the desk exposing
  people at random and make the player's own correlation work worthless.
- **Recovery room ratio** — mental recovery must stay meaningfully slower than
  physical, and specialist rooms (therapy, infirmary) are excluded from the
  per-room check so that a dedicated room's own rates do not trip it.
- **Required keys per rule table** — every key a system reads must exist, so a typo
  cannot silently fall back to a default and quietly delete a penalty.
- **Id space disjointness** — each key-value table owns its own band, enforced rather
  than maintained by hand.

The general principle: a balance error that produces no error message is the worst kind,
so each one is caught at build time.

## What is deliberately not here yet

- Stage 4: mission maps, fog of war, the alarm meter. `MissionProgress` is a seam —
  it applies the mole leak roll and stages leak difficulty, but nothing generates
  missions yet, so the phase has no work to do in practice.
- Stage 5: save format, migrations, `ReplayVerifier`. `WorldStateSerializer` is a
  fingerprint-grade canonical dump, not a save file; it exists to make the byte-level
  determinism guarantee checkable.
- Stage 7: Unity. The table binaries are copied into `Assets/StreamingAssets` there.

## Where to read next

- `docs/MOLE_DESIGN.md` — why the mole is a fair mystery, the Heat/mission-log
  reasoning chain, and what breaks the correlation if you tune it.
- `docs/FLOORPLAN_UI.md` — the Stage 8 contract for the base: slot ↔ screen mapping,
  how adjacency is derived, what Presentation owns now that Core does not, and three
  gaps to close before the UI is built.
