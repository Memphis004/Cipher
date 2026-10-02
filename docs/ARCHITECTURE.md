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
                              │                                   v       │
                              │   GameEvent ───────────────────────┘       │
                              │                                           │
                              │   WorldState (serializable root)           │
                              │     Seed · Clock · Resources              │
                              │     BaseLayout · Agents · Recruits        │
                              │     Missions · Contracts · Flags          │
                              │     RngStreams                            │
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

## Time and the tick pipeline

`GameClock` owns a single `Tick` (a `long`, hourly granularity: 24 ticks per day,
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

### Phase implementations

All six phases are implemented as of stage 3 (`Phases.CreateDefault`).

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

Core stores no world-space positions, meshes, or camera data (knowledge.md rule 10).
This is not only a Unity-boundary concern: a position mutated by anything other than a
command is not replayable, so any coordinate in Core is determinism debt.

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

## Determinism

Three mechanisms, in order of how much they matter:

1. **Named RNG streams.** `RngStreams` derives five independent generators from the
   world seed — World, Mission, Event, Recruit, Trait. Without this, one extra roll
   in trait generation would shift every mission map afterwards, invalidating saved
   replays and making bugs nearly impossible to bisect. Each command receives the
   stream it needs; none shares a global generator.
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
