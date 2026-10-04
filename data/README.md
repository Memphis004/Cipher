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
| `node_interior_template` | 10181–10198 | what a node's interior holds; *not* the tactical `room_template` |
| `interactable_type` | 10091–10098 | one row per `InteractableKind` |
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

### Stage-4 mission tables

Also keyed by `rule_key`, same reasoning: these are formula constants rather than
content, and the keys are what `SimulationRules` asks for by name.

| Table | Id range | Notes |
|---|---|---|
| `fog_rule` | 10151–10157 | reveal-radius formula and the scout/gadget/terminal bonuses |
| `node_interior_rule` | 10171–10179 | how many containers, guards, doors and traps a node's interior holds |
| `site_gen_rule` | 10199–10222 | every tunable the stage-4a site generator reads |
| `intel_rule` | 10223–10241 | every tunable the stage-4b intel system reads |
| `goap_rule` | 10242–10251 | every tunable the stage-4d GOAP planner reads; keyed by `rule_key`, not id |

`site_gen_rule` is the whole balance surface of the building generator: how narrow a
room may be, how a floor is partitioned, how likely a door is locked, how many vertical
links a floor gets, how wide the protected route on a poisoned report is, and how long the
trip to a forward command post costs. All of it is here rather than in Core because the
generator's invariants are only as good as the numbers feeding them — a designer who
cannot see the light span per emitter cannot reason about whether a floor will be dark.

`intel_rule` holds the reveal curve itself. The five band edges
(`intel_band_entrance_max` … `intel_band_full`) are the numbers written out in
`knowledge.md` rule 17 as "0-24 / 25-49 / 50-74 / 75-99 / 100", and they are here
rather than hard-coded in Core for the same reason: a designer retuning how much of a
building an operation buys should be editing a number in a spreadsheet, not recompiling.
`TableValidator` requires the five edges to be strictly ascending and to end at 100,
because out-of-order bands return the wrong band for a stretch of percentages and the
no-leak test then fails for a reason nobody can see.

The discovery formula's coefficients live here too:
`intel_infiltration_points_per_reduction`, `intel_social_points_per_reduction` and
`intel_security_grade_divisor` are the three levers on how much a good infiltrator and a
heavily guarded site matter. `intel_discovery_chance_min` being 0 rather than 1 is a
deliberate statement that "this agent will not be found at this site" is a real,
achievable outcome, and not a number the clamp refused to produce.

The poison controls are bounded on purpose:
`intel_poison_max_locked_doors` caps the false locks absolutely as well as
`intel_poison_door_percent` capping them proportionally, so a forty-room site and a
two-room site feel like the same gamble rather than a wall of lies and a free pass.
`intel_holding_room_percent` must sit above `intel_band_details_max`, so where a prisoner
is being held is never revealed before everything else about the site is.

`fog_rule` holds the reveal-radius formula's constants rather than having them in Core,
because the whole reason a specialist is worth deploying is a ratio between two of
them — which is exactly the kind of number a designer will want to try changing.
`fog_best_infiltration_weight_percent` must stay above
`fog_average_infiltration_weight_percent`, and `TableValidator` fails the build if it
does not, because reversing them makes team composition stop mattering entirely.

`mental_ratio_percent` lives on a `recovery_rule` row rather than being a constant,
but only the first row's value is meaningful — it is the organisation-wide ratio, and
the validator reads it from there. Per-room percentages are the room's own rates.

### Tactical tables

The continuous-building model. `room_template` here is the *tactical* room template —
rooms the building generator partitions floors into. It is a different table from
`node_interior_template`, which describes what goes inside an abstract mission node on
the stage-4 map. The two were briefly the same name; they are genuinely different
things and the names are now distinct.

Id bands follow the same `1x0xx` convention as the stage-3 tables, 50 apart so a table
can grow without renumbering its neighbours.

| Table | Id range | Notes |
|---|---|---|
| `site_template` | 11001–11015 | 15 sites, tiers 1–4; `allowed_room_ids` → `room_template` |
| `lighting_profile` | 11101–11106 | `emitter_ids` → `light_source` |
| `light_source` | 12251–12258 | radius in **centimetres**, matching `Fixed32` |
| `room_template` | 12001–12018 | `width_min/max` in **lane units, 1 unit = 1 m** |
| `connection_type` | 12051–12057 | one row per `ConnectionKind` |
| `noise_profile` | 12301–12322 | referenced by actions, weapons and throwables |
| `goap_goal` | 12201–12220 | `priority_curve`, `satisfaction_condition` |
| `goap_action` | 12151–12173 | preconditions/effects are declared world-state keys |
| `guard_archetype` | 12101–12112 | `goal_set_id` → `goap_goal` |
| `agent_role` | 12351–12361 | squad roles and their auto behaviours |
| `tactical_action` | 12401–12434 | the player's whole action vocabulary |
| `throwable` | 12451–12458 | `gadget_id` → `gadget`, 0 = improvised |
| `melee_weapon` | 12501–12509 | `gadget_id` → `gadget`, 0 = improvised |
| `sleeper_op` | 12551–12554 | one row per site tier |
| `capture_site` | 12601–12604 | where captured agents are held |

**`room_template.npc_tags` decides who can be found inside a room.** It is a separate
column from `tags` because they answer different questions: `tags` describes the *place*
(the intel system reports it, `capture_site.holding_room_tags` selects on it), while
`npc_tags` describes the *occupancy* — whether a static guard or a civilian can be placed
there at all. A corridor tagged `guard` is a place where a sentry may stand; it does not
mean one is standing there now.

Only two tags exist, `guard` and `civilian`, and the validator rejects anything else. An
unknown tag is not a harmless typo here: it silently removes every guard from every room
that carries it, and the site still generates, still validates, and is just quietly empty.
Corridors, lobbies and storeroos are all marked `guard` as well as the rooms whose names
sound guarded, because a building whose only guards stand in the office is a building
whose corridors are free. `TableValidator` additionally checks that every site template
promises a guard count it can actually satisfy and a civilian count it can place, so a
site cannot advertise inhabitants that no allowed room template permits.

**Two different length units, deliberately.** `room_template.width_min/max` is in lane
units (1 unit = 1 metre) because the generator lays rooms out along a floor. Everything
else — vision range, hearing range, noise radius, light radius, throw range, movement
per step — is in **centimetres**, because that is what `Fixed32` stores. Mixing the two
is the single easiest mistake to make in these tables and the validator cannot catch it,
so it is written down here instead.

### Balance rules that live in the validator: tactical

Each of these catches a data combination that produces no error at runtime — the site
generates, the mission loads, and the game simply does less than the table says.

- **`site_template`**: every objective tag must exist on a room the site is allowed to
  build, every allowed room must fit the site's narrowest floor, at least two rooms
  must be placeable, and a tier-1 site may not offer a forward command post.
- **`room_template`**: `width_max ≥ width_min` (otherwise the room can never be
  generated), all three tier weights positive, and no `VerticalOnly` door rule on
  `Any` floors — that combination makes every floor using it a sealed box.
- **`goap_action`**: every precondition and effect must name a **declared world-state
  key**, an action must have both a precondition and an effect, and every required
  archetype tag must be carried by some archetype. An action keyed on a flag nobody
  publishes can never run, and the planner pays for it forever.
- **`tactical_action`**: a `base_dc` with no `skill_used` is a roll the player cannot
  influence; and a lethal action with no heat, evidence or witness cost is a moral
  stance hard-coded into the data, which knowledge.md rule 19 forbids.
- **`light_source`**: a `Lit` emitter that can be neither destroyed nor switched off is
  unstealthable.
- **`connection_type`**: a `skill_dc` with no `requires_skill` has nothing to resolve.
- **`noise_profile`**: attenuation above 100% would make a sound grow as it crosses
  more of the building.

### Two failures this validator caught in its own first draft

Worth recording, because both were invisible at runtime and both would have shipped:

- Six `node_interior_template` rows promised `guard_max > 0` for node rooms whose tags
  did not allow `Guard`, so the generator placed guards the room's own data forbade.
- Five `node_room` rows advertised a loot table in rooms whose tags allowed no
  `Container`, so `has_loot` — a promise the player sees while merely scouted — was a
  lie. Fixed by widening `allowed_room_tags`, and both now have standing checks.

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
| `InteractableKind` | `interactable_type.kind` (stage 4) |
| `LightLevel` | `light_source.intensity_level`, `room_template.default_light_level` |
| `FloorKind` | `room_template.valid_floors` |
| `ActionCategory` | `tactical_action.category` |
| `DoorRule` | `room_template.door_positions_rule` |
| `ConnectionKind` | `connection_type.kind` |
| `GuardRole` | `guard_archetype.role` |

> `LightLevel` is **Dark / Dim / Lit**, three bands rather than a 0–100 number, because
> vision, noise reach and rendering all branch on the category. A designer tuning "can a
> guard see a crouched agent in shadow" wants three named bands, not a slider.

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
- `site_template.objective_room_tags`, `site_template.allowed_room_ids`
- `room_template.tags`, `lighting_profile.emitter_ids`
- `goap_action.preconditions`, `goap_action.effects`, `goap_action.required_archetype_tags`
- `goap_goal.priority_curve`, `goap_goal.satisfaction_condition`, `goap_goal.valid_archetypes`
- `agent_role.auto_behaviour_set`, `agent_role.allowed_orders`, `capture_site.holding_room_tags`

Two of these are **not** lists of numbers, and are parsed by name rather than by id:

- `goap_action.preconditions` / `.effects` are comma-separated **world-state keys**,
  optionally negated with a leading `!` (`"AtPost,!HasIntruder"`). They must resolve
  against the key set declared in `TableValidator.KnownGoapKeys`, which is where a new
  reaction is registered.
- `capture_site.holding_room_tags` are `room_template` tags.

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

- 652 keys, identical sets in both files.
- Thai is the default language; `en` is a parallel column, not a fallback.
- Every `name_key` and `desc_key` referenced by a table must exist in **both**
  files with non-empty text. The validator fails the build otherwise.
- Keys referenced from C# (presenters, services, tooltip text) are not discoverable by
  reflection over the table data, so they are declared by hand in
  `TableValidatorTests.StageEightUiKeys`. The validator asserts
  `table-referenced ∪ declared == actual keys` in both directions, so an orphan key fails
  just as hard as a missing one. Adding a UI key means adding it to both CSVs **and** to
  that list.

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
- every rule key the simulation reads is **present**, listed in `TableValidator` rather
  than derived from Core so the check can still fire if a system loses interest in a key
  (`RequiredSiteGenKeys`, `RequiredIntelKeys`)
- numeric sanity: costs > 0, security/alarm in range, skill levels contiguous,
  heat tiers strictly ascending, alarm non-decreasing success→partial→failure
- balance relationships that would otherwise fail silently — see above

It reports **every** failure at once rather than the first, so a designer sees
all six broken references in one run instead of one per build.
