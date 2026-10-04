# knowledge.md — Project CIPHER working rules

Created at Stage 1 from the rules stated in the Stage 1 brief. Every rule here is
binding on every later stage. If a later brief contradicts one of these lines,
stop and ask before writing code.

## 1. Project shape

- Single-player Steam game. Simulation-first: the game must be fun headless in a
  terminal before a single hour of art or UI is spent (Stage 6).
- Stage order is fixed and must not be swapped: 1 → 2 → 3 → 4 → 5 → 6.
  Stages 7–10 are comparatively independent, but 8 must precede 9.
- `ProjectSpy.Core` is a pure simulation library targeting netstandard2.1 so the
  same assembly runs in Unity, in the console simulator, and in tests.

## 2. The Core must not reference UnityEngine

- **Core must not reference `UnityEngine`** or any other Unity type. No
  `MonoBehaviour`, no `Vector3`, no `Debug`, no `Time`, no serialization via
  Unity's serializer. Core is plain C#: structs, classes, records, arrays.
- The dependency direction is one-way: Presentation → Core. Core never calls
  back into Presentation.
- Core also must not reference: `System.Random`, `DateTime`, `Guid.NewGuid`,
  `File.`, `Directory.`, `Environment.`, or non-deterministic floating point in
  rule paths. See rule 6.
- A Roslyn analyzer or a reflection-based unit test over the Core assembly must
  fail the build if a forbidden type appears (added in Stage 5; the grep is due
  in Stage 5 item 6).

## 3. No magic numbers in Core after Stage 2

- Every tuneable value lives in a CSV table compiled by Luban into
  `ProjectSpy.Tables`. Once Stage 2 lands, Core holds no hand-typed balance
  numbers — not a rate, not a cost, not a threshold, not a multiplier.
- Constants that are structural rather than tuneable (ticks per day, grid width)
  are permitted in Core and are documented as such.

## 4. Core never produces English prose for the player

- A rejected command returns a `ReasonCode` enum value plus args. The
  Presentation layer maps that code to localized text.
- Same rule for anything else a player reads: Core returns keys, ids and
  structured values, never sentences. Thai is the default language; en is a
  parallel column.

## 5. Out-of-scope bodies must be explicit

- Where a body is out of scope for the current stage, throw
  `NotImplementedException` and carry a `// TODO(stage-N):` comment naming the
  stage that will implement it.
- Never leave a silently empty method body. Silence reads as "implemented".

## 6. Determinism is a hard requirement

- The same seed plus the same ordered command log must produce a byte-identical
  `WorldState`. This is verified by test and later by the replay verifier.
- All randomness goes through `IRng`, and each subsystem gets its own named
  stream from `RngStreams` (World, Mission, Event, Recruit, Trait) so that
  adding a roll in one system cannot shift another system's sequence.
- Rule paths use integer or fixed-point arithmetic. `float`/`double` ordering
  differences between platforms are a determinism bug, not a rounding detail.
- A tick processing order that is part of the save contract (RoomConstruction →
  Training → Recovery → MissionProgress → EventChecks → StatusDecay) must be
  fixed, documented and asserted by a test.

## 7. Data integrity is enforced by tests, not by discipline

- Foreign keys, weights and localization keys are validated by a test that fails
  the build when a designer makes a bad CSV edit. A broken table must never
  reach a playable state.
- Loading a save whose ids no longer exist quarantines them into an integrity
  report rather than crashing or silently dropping them.

## 8. Layering

- Presentation (Unity, stage 7+) owns input, rendering, audio and localization.
  It reads Core and issues commands to Core; it never mutates `WorldState`
  directly.
- All mutation of the world goes through `ICommand.Execute` so that every state
  change is logged and therefore replayable.

## 9. Scope discipline

- Smallest correct change. Match the conventions already in the file you are
  editing. Do not refactor code outside the task's blast radius.
- Report findings plainly. The Stage 6 design review and the Stage 10 review
  both ask for bluntness over comfort.

## 10. Core is presentation-agnostic

- **Core never stores world-space coordinates, meshes, or camera data.** No
  positions, sizes, rotations, transforms, renderers, colliders or viewport
  concepts anywhere in Core — including in types that exist to *support*
  rendering. This extends to the base layout: a room is identified by a slot
  index and a layer, never by a grid cell.
- A mission node's interior is described abstractly as `RoomContents`: a list
  of interactables (container, door, terminal, guard, trap, exit), each with a
  stable id, a type, a state, and an abstract slot index.
- **The presentation layer decides how slots map to 2D, 2.5D or 3D
  positions.** Core defines which slot an object occupies and nothing about
  where that is on screen.
- Walking inside a room is presentation only. **Every rule-affecting action is
  an `ICommand` with a tick cost.** If Core stored a position for an agent, the
  player could move without issuing a command, and the move would not be in the
  command log — which breaks rule 6, because a replay would not reproduce it.
- Spatial questions that *are* rules (which rooms are adjacent, which layer a
  room is in, what an interactable's state is) are stored as abstract data in
  Core. Adjacency is explicit data, not derived from coordinates.

### Why

Three reasons, in order of weight:

1. **Determinism.** A position mutated by anything other than a command is not
   replayable. Core would hold state that no log describes.
2. **One simulation, many presentations.** The same assembly has to run the
   headless simulator (rule 1) and Unity. Coordinate-free Core is what makes
   that the same code rather than two implementations.
3. **It is testable.** A rule that reads "the terminal in slot 3 is locked"
   can be asserted directly. A rule that reads "the terminal at x=4.2, y=-1.7
   is locked" needs a spatial setup to say anything at all.

## Presentation-agnostic rule (added before Stage 4)
Core never stores world-space coordinates, meshes, animation or camera data.
A mission node's interior is an abstract `RoomContents`: a list of interactables
(container, door, terminal, guard, trap, camera, objective, exit), each with a stable id,
a type, a state and an abstract SlotIndex. Presentation maps slots to 2D, 2.5D or 3D
positions however it likes. Walking around inside a room is presentation only.
Every rule-affecting action is an ICommand with a tick cost, validated by Core.

## Lazy content rule
Contents of a node the team has not entered DO NOT EXIST in memory. They are generated
on first entry from an RNG derived ONLY from (MapSeed, nodeId, "contents"), never from a
shared stream. Consequence: the order in which a player explores must never change what
any room contains. This is tested.


### Enforcement

`CorePurityTests` rejects coordinate- and render-shaped types on Core's public
surface, and the type-naming conventions below are part of the contract:

| Concept | Core name | Never |
|---|---|---|
| Where something sits | `SlotIndex`, `Layer` | `x`, `y`, `z`, `position`, `transform` |
| How big it is | `SlotCount`, `Tier` | `width`, `height`, `scale`, `bounds` |
| How it looks | `TypeId`, `NameKey` | `mesh`, `sprite`, `prefab`, `material` |
| Who can see it | `Revealed` | `visible`, `rendered`, `occluded` |
