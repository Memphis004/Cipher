# The base floorplan — what Presentation must own after the slot migration

Stage 3 removed every coordinate from Core (knowledge.md rule 10). `BaseLayout` is now
a set of abstract slots arranged into layers, and adjacency is data rather than
something derived from geometry. That moves real work across the boundary.

This document is the contract for Stage 8. It covers what Core now demands, what
Presentation must compute that Core used to, and three gaps the migration exposed that
should be closed before the UI is written against them.

---

## 1. The contract in one line

```
BuildRoomCommand(TypeId, Layer, SlotIndices[], AdjacentRoomIds[], NameKey, TotalCost)
```

Everything else follows from those six values. `LayerCount` (12) and `SlotsPerLayer`
(24) are the layout's size; Core treats them as counts and nothing more.

## 2. What Core used to do, and no longer does

| Responsibility | Before | Now |
|---|---|---|
| Slot geometry | `Room.GridX/GridY/Width`, `Width x Height` grid | `Room.SlotIndices`, `Room.Layer` |
| Slot occupancy | flat `int[width*height]` array | `Dictionary<layer, Dictionary<slot, Room>>` |
| **Adjacency** | derived: `other.GridX == MaxX + 1` | **supplied by the caller** |
| Containment | `Room.Contains(x, y)` | `Room.OccupiesSlot(slot)` |
| Row queries | `RoomsAtDepth(y)` | `RoomsAtLayer(layer)` |
| Footprint | caller passed a width | caller passes a slot list |

Only one row is a genuine transfer of responsibility: **adjacency**. Everything else is
a rename or a direct substitution.

## 3. What Presentation must now compute

### 3.1 Slot → screen, and screen → slot

Core has counts, not positions. Presentation owns both directions of the mapping.

**Forward** — how slot `(layer, slot)` is drawn. Choices are entirely yours: slots per
row, pixel size, whether layers stack downward (isometric), zoom, pan.

**Inverse** — how a pointer position becomes a candidate `(layer, slot)`. This is the
one that needs care. A half-slot rounding error means the player cannot place a room
where they aimed, and the bug will read as "the game misplaced my building" rather than
as a UI defect. Derive the slot from the layer's own transform, not from a global
inverse, so layers can be drawn with independent offsets.

**Recommendation:** make the layout a pure function of
`(LayerCount, SlotsPerLayer, PresentationLayoutConfig)`. No per-save geometry. Because
Core stores nothing about position, this is the only way a returning player's base
looks the same as when they left — and a layout that must be persisted is a layout
that can drift out of sync with the save.

### 3.2 Adjacency — the real work

Core no longer infers adjacency from distance. Whoever calls `BuildRoomCommand` must
say which rooms the new one touches.

**Derive it at drop time from geometry the player can see.** The player should not
declare adjacency; it is a consequence of where they put the thing, and asking them to
state it separately would be asking them to describe a picture.

The rule Core's own tests and harness use, and which the UI should mirror exactly:

> A candidate footprint is adjacent to every room in the **same layer** whose slot
> range begins at `lastSlot + 1` or ends at `firstSlot - 1`. Lateral only.

The reference implementation is `NeighboursFor` in
`src/ProjectSpy.Core.Tests/GridCommands.cs`. `World.Place` in `StageThreeHarness.cs`
uses the same rule.

Two properties of this rule are load-bearing:

- **Lateral only.** A room stacked directly above is adjacent on screen but was never a
  legal merge, and `Room.CanMergeWith` still requires the same layer. If the UI derives
  adjacency from 2D screen proximity it will produce vertical edges that Core accepts
  and then silently refuses to merge along.
- **Same layer only.** `CheckPlacement` rejects a neighbour in another layer with
  `PlacementError.NeighbourInOtherLayer`, so a derivation that ignores layers produces
  a confusing rejection on an otherwise valid placement.

**Recommendation:** extract this rule into Core as
`BaseLayout.DeriveLateralNeighbours(layer, firstSlot, lastSlot)` and call it from the
UI. Right now the rule exists twice — once in the test harness, once (soon) in
Presentation — and two implementations of "which rooms does this touch" is exactly the
kind of divergence that passes every test and then behaves differently in the game.
See §6.

### 3.3 Footprint — read it, do not invent it

`room_type.csv` has a `width` column with real values (2 slots for five room types, 3
for fifteen, 4 for two). **Nothing in Core reads it.** Room width is currently supplied
by the caller, so the UI would have to hardcode or re-derive it and could disagree with
the table.

**Recommendation:** add `RoomDefinitions.SlotWidthFor(world, roomTypeId)` and have the
placement path use it. Drag distance should move the footprint, not resize it — a
room's size is a property of the room type, and letting the player resize it makes
`room_type.width` decorative.

### 3.4 Ghost preview and validation

`CheckPlacement` is side-effect free and cheap. Call it on every drag update and map
the result:

| `PlacementError` | Player-facing meaning |
|---|---|
| `None` | legal |
| `NoSlots` | the room needs at least one slot |
| `LayerOutOfRange` | that layer does not exist |
| `SlotOutOfRange` | off the end of the layer |
| `SlotOccupied` | something is already there |
| `LockedByStory` | not unlocked yet |
| `LayerTooShallow` | this room type cannot go that shallow |
| `UnknownNeighbour` | should be unreachable from a correct derivation |
| `NeighbourInOtherLayer` | a bug in your adjacency derivation — see §3.2 |

All of these are pure Core values; `BuildRoomCommand.Validate` already converts them to
`CommandReason` members, and `CommandResult.MessageKey` gives you the localization key.
Core produces no prose (rule 4).

### 3.5 Depth as a cost decision

`Layer` is a real rule input: `DepthCostModifier[layer]` scales build cost, with the
default curve at 100% + 10% per layer, capped at 200%. `RoomDefinitions` populates
`MinimumDepthByType` from `room_type.min_depth`.

Presentation should make depth legible as a cost, because that is what it is:

- a depth axis or layer selector, not a hidden modifier,
- the current multiplier shown next to the quoted price,
- layers below a room type's `min_depth` visibly unavailable rather than silently
  rejected on drop.

**Recommendation:** explicit layer selection. Auto-picking the nearest free layer is
fewer clicks but removes the only decision that layer depth represents.

### 3.6 Merge feedback

`FindMergeCandidates(roomId)` returns the rooms that would fold in;
`TryMergeAdjacent(roomId)` does it and always returns the lowest-slot member of the
group, so the result does not depend on which room was clicked.

Show the player the outcome before committing: the union of slot sets, the resulting
`SlotCount`, and which rooms disappear. `CanMergeWith` requires same type, same level,
same layer, declared adjacency, and both finished building — the last one surprises
people, since a room under construction cannot be merged into.

### 3.7 Readouts

`OccupiedSlotCount`, `SlotCapacity`, `RoomsOfType`, `RoomsAtLayer`, `NeighboursOf`,
`AreAdjacent`, `RoomInSlot`. `Rooms` is in placement order, which is stable across a
session but is not a display order — sort for presentation.

## 4. Persistence

This is the largest new responsibility. Because Core holds no geometry:

- The visual layout must be reconstructible from `(LayerCount, SlotsPerLayer)` plus
  Presentation's own config. Derive it; do not store it.
- If a player resizes the window or changes zoom, the slot layout must not change. Zoom
  is a camera concern; slot geometry is not.
- Saves from before the migration referenced `GridX/GridY/Width`. Stage 5 needs an
  explicit mapping to `(Layer, SlotIndices)`. There is no safe default: a grid cell is
  not a slot, and guessing would place rooms somewhere the player did not leave them.

## 5. Events changed shape

| Event | Was | Is |
|---|---|---|
| `RoomBuilt` | `(…, GridX, GridY, Width, CostPaid)` | `(…, Layer, SlotCount, CostPaid)` |
| `RoomMerged` | `(…, NewWidth, NewGridX)` | `(…, Layer, SlotCount)` |

A UI that was reading a position from these now reads a layer and a slot count, and
must look the room up in the layout to draw it. This is the intended direction, but it
is a breaking change for any view model built against the old payloads.

## 6. Gaps this surfaced

Three, all found while writing this and none of them fixed yet. They are load-bearing
for the UI contract, which is why they are listed rather than left as notes.

### 6.1 `room_type.width` is never read

Declared, populated, validated, unused. The footprint is caller-supplied, so the UI
would have to duplicate table data it could read. Fix: expose `SlotWidthFor` and derive
the footprint in Core.

### 6.2 `room_type.merge_group` is never read by the merge rule

The column exists, the validator requires it non-empty, and all 22 room types currently
have a distinct value — so today `merge_group` and "same `TypeId`" agree exactly. The
moment a designer gives two room types the same merge group, the code will ignore it
and the merge rule will quietly contradict the data. Fix: `CanMergeWith` should compare
merge groups.

### 6.3 `BuildRoomCommand` holds its collections by reference

`SlotIndices` and `AdjacentRoomIds` are stored as `IReadOnlyList<T>` but the record
keeps the caller's instance. `GameSession.Execute` puts the command itself into the
command log and the replay log. A UI that reuses and mutates a scratch list between
drags would therefore **retroactively change a command that is already recorded**, and
the replay would diverge — silently, and only after a reload.

`IReadOnlyList<T>` prevents the *command* from mutating the list, not the caller from
mutating the list it passed in. Fix: copy defensively when constructing the command, or
have the UI pass a fresh array each time. The former is safer because it makes the
whole codebase safe rather than relying on every caller to behave.

### 6.4 `PlacementError.UnknownRoomType` is never returned

`CheckPlacement` validates the unlock set but never checks the type id against the
table. With the default empty unlock set — which means "everything unlocked" — an
unknown type id is placed successfully. The enum member advertises a check that does not
exist. Low severity (the type id comes from a build menu), but the dead member is a
trap for whoever wires up the error mapping in §3.4.

## 7. Do not reintroduce coordinates

The obvious pressure once the UI owns geometry is to store a position "just for the
UI" — on the agent, on the room, in a helper struct in Core. Every one of those breaks
determinism (rule 6) and none of them is needed: `WorldState` holds slots and layers,
Presentation holds the mapping.

If the UI needs to remember something spatial between sessions, that is a
Presentation-side save artifact. It does not belong in `WorldState` and it does not
belong in the state hash.

## 8. Checklist for Stage 8

- [ ] Slot ↔ screen mapping defined as a pure function of layout config
- [ ] Pointer hit-testing lands on the slot the player aimed at
- [ ] Adjacency derived by one shared rule, lateral and same-layer only
- [ ] Footprint read from `room_type.width` (after §6.1)
- [ ] Ghost preview calls `CheckPlacement` and maps every error to text
- [ ] Depth shown as a cost, with `min_depth` limits visible before the drop
- [ ] Merge preview shows the resulting footprint and slot count
- [ ] Layout reconstructible from counts alone, zoom-independent
- [ ] `RoomBuilt` / `RoomMerged` consumers updated to layer + slot count
- [ ] §6.1–6.3 addressed before the UI is built against them
