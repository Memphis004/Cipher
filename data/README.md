# data/ — tuneable content

Every balance number in the game lives here as CSV and is compiled by
[Luban](https://github.com/focus-creative-games/luban) into C# beans plus a
binary blob. Core reads those beans; it must never contain a hand-typed balance
constant (see `knowledge.md` rule 3).

## Workflow

```bash
pwsh tools/gen.ps1     # CSV -> src/ProjectSpy.Tables/Gen + assets/data/tables
dotnet build           # generated beans compile into ProjectSpy.Tables
dotnet test            # TableValidator proves the data is internally consistent
```

`gen.ps1` refuses to run without `tools/luban/Luban.dll` and prints download
instructions. The DLL is gitignored; only the generated **C#** is committed
(Unity needs the types at compile time), while the **binary** is regenerated on
every machine.

Output locations:

| Output | Path | Consumed by |
|---|---|---|
| C# beans | `src/ProjectSpy.Tables/Gen/` | `ProjectSpy.Tables` (Core, tests, Sim, Unity) |
| Binary | `assets/data/tables/*.bytes` | `TableService`, used by console projects and tests |

Stage 7 copies `assets/data/tables/` into `UnityProject/Assets/StreamingAssets/`.
The canonical copy lives outside `UnityProject/` on purpose: one set of bytes
means the balance simulator and the shipping game cannot drift apart.

## CSV format

Luban CSV is **not** plain data. The first line names the fields and must be
mirrored exactly by a type line:

```
##var,id,name_key,build_cost
##type,int,string,int
,4001,room.gym,1200
,4002,room.range,1500
```

Two things trip people up:

- Every **data row starts with a leading comma**. That empty first cell is the
  marker column and must be present, or the row shifts by one and you get a
  baffling "X is not an int value" error on an unrelated column.
- The `##type` row must have **exactly the same number of cells as `##var`**.
  A single extra type silently shifts every type to the right, which surfaces as
  a string value failing to parse as an int several columns away.

Quoted fields (`"a,b"`) work normally for comma-separated lists. Values are
culture-invariant: `1.5`, never `1,5`.

## Id spaces

Id spaces are **disjoint by construction** — each table owns a private band, so a
stray id cannot accidentally cross-reference from one table into another.
`TableValidator.ValidateIdSpacesAreNonOverlapping` fails the build if they ever
collide.

| Table | Id range | Notes |
|---|---|---|
| `agent_class` | 1001–1004 | 4 classes |
| `agent_name` | 2001–2560 | first/surname/codename pool, keyed by `kind` |
| `trait` | 3001–3024, 3101–3106 | 30 traits; hidden traits use the 31xx band |
| `skill_curve` | keyed by `level` | levels 1–20, contiguous |
| `room_type` | 4001–4022 | 22 rooms |
| `room_upgrade` | 5001–5044 | unique per (`room_type_id`, `level`) |
| `mission_type` | 6001–6018 | 6 objectives × 3 tiers |
| `map_gen_rule` | keyed by `tier` | tiers 1–3 |
| `node_room` | 7001–7018 | mission node room types |
| `mission_event` | 7101–7151 | 51 events |
| `loot_table` | 7301–7318 | `list` mode: many rows per table id |
| `item` | 8001–8022 | |
| `gadget` | 8501–8512 | |
| `contract_offer` | 9001–9012 | |
| `heat_tier` | keyed by `threshold` | thresholds strictly ascending, lowest ≤ 0 |

### Stage-3 balance tables

The stage-3 key-value tables each own a **10-wide band** starting at `100x1`. Keep at
least 10 ids of headroom per table: `counter_intel_rule` has already outgrown one and
forced `burnout_rule` up a band.

| Table | Id range | Notes |
|---|---|---|
| `skill_cap` | 10001–10005 | one per skill |
| `recovery_rule` | 10011–10015 | per room type; carries `mental_ratio_percent` |
| `training_rule` | 10021–10025 | per room type; cost and affinity |
| `economy_rule` | 10031–10046 | keyed by `rule_key`, not id |
| `morale_band` | 10051–10054 | the coarse band the UI is allowed to see |
| `loyalty_drift` | 10061–10071 | one row per `loyalty_drift_condition` |
| `loan_tier` | 10075–10077 | |
| `loyalty_threshold` | 10081–10084 | ascending; lowest is the most severe |
| `recruit_rule` | 10101–10109 | keyed by (`hr_level`, `reputation_tier`) |
| `counter_intel_rule` | 10121–10133 | keyed by `rule_key`, not id |
| `burnout_rule` | 10141–10147 | keyed by `rule_key`, not id |

`mental_ratio_percent` lives on a `recovery_rule` row rather than being a constant,
but only the first row's value is meaningful — it is the organisation-wide ratio, and
the validator reads it from there. Per-room percentages are the room's own rates.

### Balance rules that live in the validator, not in review

Some balance mistakes produce no error message anywhere: the system simply stops
working, or starts working too well. Those are checked at build time instead.

- `counter_intel_rule`: a case must be able to conclude (best case clears
  `expose_threshold`) **and** must not conclude on average (expected case does not).
  The second half matters as much as the first — evidence accrues identically whoever
  you investigate, so a low threshold would have the desk expose people at random.
- `recovery_rule`: mental recovery stays meaningfully slower than physical, and
  specialist rooms (therapy, infirmary) are excluded from the per-room check so their
  own rates do not trip it.
- Every rule table's required keys must exist, so a typo cannot fall back to a default
  and silently delete a penalty.

The 31xx band for hidden traits is a deliberate convention: anything hidden
(Mole, Deserter, …) is identifiable at a glance in a diff, and a designer cannot
accidentally renumber one while editing the visible traits.

`loot_table` is registered in `list` mode because one loot table id legitimately
spans several rows. It is the only such table.

## Enums

`Defines/__beans__.xml` defines the enums. It is XML, not `__beans__.csv`,
because Luban 5.1.0's CSV loader cannot express nested enum definitions
(verified: it reports *"can't find schema loader for type: extName:csv"*).

Enum values are explicit and **append-only** — a Luban binary stores an enum as
its integer value, so renumbering one invalidates every existing `.bytes` file.

| Enum | Used by |
|---|---|
| `RoomCategory` | `room_type.category` |
| `SkillKind` | `room_type.trains_skill`, `mission_event.skill_checked` |
| `EffectType` | `room_type.effect_type` |
| `TraitPolarity` | `trait.polarity` |
| `ObjectiveType` | `mission_type.objective_type` |
| `DifficultyClass` | `mission_event.difficulty_class` |
| `GadgetSlot` | `gadget.slot` |
| `ItemCategory` | `item.category` |
| `NameKind` | `agent_name.kind` |
| `NameRegion` | `agent_name.region` |

> `SkillKind` exists in **both** Core and the generated tables. Core's version is
> the five skills an agent has; the table's adds `None` meaning "this room trains
> nothing". Stage 3 converts between them explicitly. Do not unify them by
> accident — the `None` member has no meaning in Core.

## Comma-separated lists

Luban has no native string-set type, so several columns are comma-separated
strings, parsed at load:

- `agent_class.preferred_room_ids`
- `node_room.tags`, `node_room.allowed_tiers`, `node_room.possible_event_ids`
- `trait.conflicts_with`

These are the main source of dangling-FK bugs, which is why the validator checks
every element of each one rather than just the field being non-empty.

## Localization

`localization/th.csv` and `localization/en.csv` map a key to text:

```
##var,key,text
##type,string,string
,room.gym,ห้องยิม
```

These are **not** Luban tables — they are read directly by
`LocalizationCatalog` so a translator can edit them without a codegen step.

- 258 keys, identical sets in both files.
- Thai is the default language; `en` is a parallel column, not a fallback.
- Every `name_key` and `desc_key` referenced by a table must exist in **both**
  files with non-empty text. The validator fails the build otherwise.

`event.*` keys are the UI label for an event; `event.nar.*` keys are the
after-action report line. They currently share wording but are separate keys so
the narrative can diverge later without touching the UI.

## What the validator checks

`src/ProjectSpy.Core.Tests/TableData/TableValidator.cs`, run by
`ShippedTables_HaveNoValidationErrors`:

- no dangling foreign key (every id in every comma-separated list resolves)
- no weight ≤ 0; loot `max_count` ≥ `min_count`
- every `name_key`/`desc_key` present and non-empty in both languages
- every `trains_skill` is a real skill, and all five skills have a room
- every mission tier has ≥ 3 usable `node_room` rows
- id spaces disjoint
- numeric sanity: costs > 0, security/alarm in range, skill levels contiguous,
  heat tiers strictly ascending, alarm non-decreasing success→partial→failure
- balance relationships that would otherwise fail silently — see above

It reports **every** failure at once rather than the first, so a designer sees
all six broken references in one run instead of one per build.
