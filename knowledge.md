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
