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
`TickPipeline.CanonicalOrder` and asserted by test. Phases are stored in a dictionary
and executed in enum order, so a phase registered later cannot accidentally run in
the wrong slot.

At stage 1 the six phases are stubs that throw `NotImplementedException`; stage 3
implements them. `TickPipeline` only executes phases that have been registered, so
the pipeline is runnable today.

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

`WorldState.ComputeStateHash()` produces a stable, integer-only fingerprint
including RNG state — two worlds that look identical but will roll differently are
not the same world, and the hash must say so.

## Where balance numbers come from

Since stage 2 no tuneable value is hard-coded in Core. Everything lives in
`data/*.csv`, is compiled by Luban into `ProjectSpy.Tables`, and is read back
through a narrow set of lookups — currently `RoomDefinitions`, which Core uses for
build time, worker slots, upkeep and upgrade costs.

Core references `ProjectSpy.Tables` directly. That is allowed and does not violate
the Unity boundary: the tables assembly is pure compiled data (beans and enums),
contains no Unity types, and targets `netstandard2.1` like everything else.

```
  data/*.csv  ──pwsh tools/gen.ps1──>  src/ProjectSpy.Tables/Gen   (C# beans)
                                         assets/data/tables          (*.bytes)
                                                  │
                                                  v
  Core ── RoomDefinitions.Find(4001).WorkerSlots ──> read at runtime
```

The accessor style is deliberately tolerant: an id that is missing from the tables
returns a documented default instead of throwing, because stage 5 deliberately
loads saves that may reference retired ids. Missing data is a build failure
(`TableValidator`), not a runtime crash.

## What is deliberately not here yet

- Stage 3: the six tick phases, weekly settlement, recruitment, loyalty, the mole.
  `Agent.AddExp` still uses a placeholder level curve, marked `TODO(stage-3)`,
  pending a `skill_curve.csv` lookup — that table exists now but the lookup is not
  written yet.
- Stage 4: mission maps, fog of war, the alarm meter.
- Stage 5: save format, migrations, `ReplayVerifier`.
- Stage 7: Unity. The table binaries are copied into `Assets/StreamingAssets` there.
