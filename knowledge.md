# knowledge.md — Project CIPHER working rules

Created at Stage 1 from the rules stated in the Stage 1 brief. Every rule here is
binding on every later stage. If a later brief contradicts one of these lines,
stop and ask before writing code.

## 1. Project shape

- Single-player Steam game. Simulation-first: the game must be fun headless in a
  terminal before a single hour of art or UI is spent (Stage 6).
- Stage order is fixed and must not be swapped: 1 → 2 → 3 (→ 3b only if needed) → 4a → 4b →
  4c → 4d → 4e → 5 → 6. Stages 7–10 are comparatively independent, but 8 must precede 9.
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
- **Two** processing orders are part of the save and replay contract. Each is fixed,
  documented and asserted by a test, and the two never interleave:
  - Strategic tick order: RoomConstruction → Training → Recovery → MissionProgress →
    EventChecks → StatusDecay.
  - Tactical step order, inside a mission only, one 100 ms step at a time:
    ApplyQueuedCommands → MovementResolution → ActionProgress → NoisePropagation →
    Perception → NpcPlanning → AlarmUpdate → DamageAndStatus → ObjectiveCheck → EventEmit.
- The base clock does not advance while a tactical mission is running. Elapsed steps
  convert to strategic ticks exactly once, when the mission closes. See rule 13.

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
- Player input is sampled ONLY at tactical step boundaries and converted into
  `ICommand`s appended to the command log. No gameplay logic — in Core or in
  Presentation — may read Unity `Input` or `Time.deltaTime`. Rendering interpolates
  between steps; the simulation never does. See rule 14.

## 9. Scope discipline

- Smallest correct change. Match the conventions already in the file you are
  editing. Do not refactor code outside the task's blast radius.
- Report findings plainly. The Stage 6 design review and the Stage 10 review
  both ask for bluntness over comfort.

## 10. Core is presentation-agnostic

- **Core never stores Unity types, render types, meshes, animation data, or
  screen/camera data.** No `Vector3`, no `Transform`, no `MonoBehaviour`, no
  renderer, no collider, no animation clip, no viewport concept anywhere in Core
  — including in types that exist to *support* rendering.
- The BASE layout stays abstract. A room is identified by a `SlotIndex` and a
  `Layer`, sized by a `SlotCount`, never by a grid cell.
- **Exception — TACTICAL space.** Core MAY store LOGICAL positions, because every
  position change is either a logged `ICommand` or the output of a deterministic
  step, so the seed plus the command log fully describes the position history:
  `TacticalPosition = { FloorIndex: int, X: Fixed32 }` (Fixed32: 1 unit = 1 cm).
  See rule 15. The exception is scoped to two places: the `Core.Tactical`
  namespace, and the **site-layout types** in `Core.Missions` (`SiteLayout`,
  `SiteFloor`, `SiteRoom`, `SiteConnection`, …), which describe a building as
  ordered `[StartX, EndX)` intervals in centimetres. It does not extend to the
  base layout or to anything render-facing — and within the exception, render
  state (mesh, sprite, renderer, texture, camera, collider) stays banned
  outright. A building's rooms occupy space; `Vector3`s do not.
- **The presentation layer decides how slots and `TacticalPosition`s map to 2D,
  2.5D or 3D positions.** Core decides where an object is in simulation terms and
  nothing about where that is on screen.
- A mission interior is a generated CONTINUOUS BUILDING, not an abstract list of
  nodes: rooms are walkable intervals on floors, and connections (Door, Stair,
  Ladder, Vent, Window, Hole) are explicit data. Adjacency is explicit data, never
  derived from screen geometry. See rule 16.
- **Every rule-affecting action is an `ICommand` with a cost** — a tick cost at the
  strategic scale, a step cost at the tactical scale. A position that moved without
  a command would break rule 6, because a replay would not reproduce it.

### Why

Three reasons, in order of weight:

1. **Determinism.** A position mutated by anything other than a command is not
   replayable. Core would hold state that no log describes.
2. **One simulation, many presentations.** The same assembly has to run the
   headless simulator (rule 1) and Unity. Core that holds no presentation types is
   what makes that the same code rather than two implementations.
3. **It is testable.** A rule that reads "the terminal in slot 3 is locked"
   can be asserted directly. A rule that reads "the terminal in room 7, at x=4.2,
   is locked" needs a generated site as setup. Tactical rules are fixed-point
   interval maths (rule 15), so they assert against plain integer ranges and need
   no rendering at all.

### Enforcement

`CorePurityTests` rejects coordinate- and render-shaped types on Core's public
surface, and the type-naming conventions below are part of the contract:

`x`, `position` and `width` are rejected everywhere on Core's public surface
**except inside the `Core.Tactical` namespace and the site-layout types in
`Core.Missions`** (rules 15 and 16), where they denote fixed-point centimetres of
simulated floor space and never anything a renderer consumes. The exemption is
split by category on purpose: coordinate-shaped words are allowed in those two
places, render-shaped words are allowed nowhere.

| Concept | Core name | Never |
|---|---|---|
| Where something sits | `SlotIndex`, `Layer`; `TacticalPosition.FloorIndex`/`X` inside `Core.Tactical`, and `StartX`/`EndX`/`X` on site-layout types inside `Core.Missions` | `transform`, `worldPosition`, `screenPosition`, `cameraPosition` |
| How big it is | `SlotCount`, `Tier`; a room's `Span` as `Fixed32` inside `Core.Missions` | `scale`, `Bounds`, `Vector3`, mesh extents |
| How it looks | `TypeId`, `NameKey` | `mesh`, `sprite`, `prefab`, `material`, `AnimationClip` |
| Who can see it | `Revealed`, `LightLevel` | `visible`, `rendered`, `occluded` |

## 11. Lazy mission content

- The detailed contents of a room the team has not OBSERVED DO NOT EXIST in memory.
  They are generated on first observation from an RNG derived ONLY from
  (MapSeed, roomId, "contents"), never from a shared stream. Observation, not entry:
  walking past a closed door reveals nothing.
- Consequence: the order in which a player explores must never change what any room
  contains. This is tested (4a order-independence test), and re-tested after save/load.
- Any room whose detailed contents have not been observed exposes no interactable
  ids through any Core API the presentation layer can reach. Intel (rule 17) reveals
  layout, room types and connections — not the unobserved interior detail this rule
  governs.

## 12. Presentation target
- 2.5D: 3D meshes viewed through a fixed side-on cutaway camera. The player never rotates it.
- Base view reference: Lobotomy Corporation. Tactical view reference: Into the Dead - Our Darkest Days.
- Unity uses URP (3D), NOT URP 2D.
- Lighting is gameplay. Core is always the authority on "can X perceive Y";
  Unity only renders what that computation already decided.

## 13. Two time scales
- STRATEGIC TICK = 1 in-game hour, 24 per day. Drives the base: construction, training,
  recovery, economy, weekly settlement, sleeper-agent progress, travel.
  Speeds: Pause / 1x / 4x / 16x / 64x. At high speed days and weeks pass quickly.
- TACTICAL STEP = 100 ms, 10 per simulated second. Active ONLY inside a tactical mission.
  Speeds: Pause / 0.5x / 1x / 2x / 4x. One real minute of play is roughly one in-game minute.
- The base clock does NOT advance while a tactical mission is running. On mission end,
  elapsed steps convert to strategic ticks once: ticks = ceil(steps / 36000).
- Speed controls at either scale only change how real seconds map to steps/ticks.
  They must NEVER change simulation results.

## 14. Direct control and determinism
- The player directly controls agents during tactical missions, switching between them.
- Input is sampled ONLY at tactical step boundaries and converted into ICommands appended
  to the command log. No gameplay logic may read Unity Input or Time.deltaTime.
- Rendering interpolates between steps; the simulation never does.
- Replay must therefore still reproduce a tactical mission step-for-step from seed + log.

## 15. Tactical space model
- Core MAY store LOGICAL positions. Core may NOT store Unity types (Vector3, Transform,
  Mesh, Bounds) or any screen/camera data.
- Cutaway view means movement is one-dimensional per floor:
    TacticalPosition = { FloorIndex: int, X: Fixed32 }   // Fixed32: 1 unit = 1 cm
  Vertical movement only through explicit Connections (Door, Stair, Ladder, Vent, Window, Hole).
- Derived systems are interval maths on that line, all fixed-point integer:
    Vision  — a perceiver covers [x - range, x + range] on its floor, clipped by occluders,
              modified by light level, posture and movement of the target.
    Noise   — origin + radius, attenuated per connection crossed and per floor crossed.
    Light   — emitters define lit intervals; an entity is Lit, Dim or Dark.
    Reach   — melee, throw arc and interaction are range checks on the same line.
- Floating point is allowed in Presentation only.

## 16. Mission structure
- A mission is a generated CONTINUOUS BUILDING, not an abstract graph of abstract nodes.
  Rooms are walkable spaces on floors; connections are real doors, stairs, vents and windows.
- Pre-mission intel comes from a Sleeper Agent (see below). In-mission perception comes from
  the agents themselves.
- Infiltration skill governs PERCEPTION RANGE and how much of an adjacent space an agent can
  read before committing to entering it. It no longer reveals abstract map nodes.

## 17. Sleeper Agent and pre-mission intel
- Before a tactical mission the player may insert a Sleeper Agent into the target site.
  Insertion takes strategic ticks and accrues an Intel percentage from 0 to 100.
- Intel percentage determines how much of the building is pre-revealed when the mission starts,
  and the reliability of that information:
    0-24   nothing but the entrance.
    25-49  floor count and rough layout; room types unknown.
    50-74  room types and connections; patrol routes unknown; some information is STALE
           (generated as it was N ticks ago and may now be wrong).
    75-99  patrol routes, guard counts, light sources, objective location.
    100    everything above, fully current, PLUS the holding location of any captured agent.
- Stale intel is a first-class concept: Core stores an IntelSnapshot with a timestamp, and the
  tactical sim may diverge from it. The UI must render known-but-possibly-wrong information
  distinctly from directly observed information. Never silently correct stale intel.
- A Sleeper Agent can be discovered: risk per tick scales with site security and inversely
  with the agent's Infiltration and Social. Discovery raises Heat, burns the agent, and may
  poison the intel (deliberately false data) without telling the player.

## 18. Tactical squad
- A mission deploys 3-5 agents. One is controlled directly at any moment; the others follow
  their assigned ROLE behaviour until the player switches to them or issues an order.
- Some missions provide a FORWARD COMMAND POST (a van or an adjacent building) that is a real
  location on the map, grants support abilities, and can itself be compromised.

## 19. Lethality is a tuning decision, not a code decision
- Killing is permitted mechanically, but every lethal act carries data-driven costs:
  heat_cost, evidence_level, mental_cost, body-discovery consequences.
- Whether the final game encourages or forbids killing is decided later by tuning those
  values. No code path may assume one answer.

## 20. NPC intelligence
- Site NPCs run GOAP (Goal-Oriented Action Planning) in Core, fully deterministic.
- Planning cost and replanning frequency must be budgeted; see the tactical stages.