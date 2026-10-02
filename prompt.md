
## Stage 1 — Solution skeleton + domain model

````text
/plan then implement

Create the workspace for **Project CIPHER**, a single-player Steam game.
Read @knowledge.md first; every rule there is non-negotiable, especially the
"Core must not reference UnityEngine" rule.

Produce compiling code. Where a body is out of scope for this stage, throw
NotImplementedException with a `// TODO(stage-N):` comment. Never leave a silent empty body.

## Repository layout
ProjectSpy/
├── ProjectSpy.sln
├── Directory.Build.props         # LangVersion latest, Nullable enable, ImplicitUsings enable
├── .gitignore                    # .NET + Unity + Rider/VS
├── knowledge.md                  # exists, do not overwrite
├── README.md
├── data/                         # Luban CSV (stage 2)
├── tools/                        # gen.ps1, sync-dlls.ps1 (stage 2 / 7)
├── docs/
├── src/
│   ├── ProjectSpy.Core/       # netstandard2.1 — pure simulation
│   ├── ProjectSpy.Core.Tests/ # net8.0 — xUnit
│   ├── ProjectSpy.Tables/     # netstandard2.1 — Luban output
│   └── ProjectSpy.Sim/        # net8.0 — headless balance simulator (stage 10)
└── UnityProject/           # created in stage 7

## ProjectSpy.Core — domain model for this stage

Foundations:
- `IRng` + `XorShift128Rng` (seeded, serializable state, `NextInt(min,max)`, `NextRoll100()`,
  `Pick<T>(IReadOnlyList<T>)`, `WeightedPick<T>(items, weights)`).
- `RngStreams` holding named independent streams: World, Mission, Event, Recruit, Trait.
- `Tick` readonly struct (long) with `Day`, `HourOfDay`, `Week`, `DayOfWeek`, arithmetic
  operators, and `Tick.FromDays(int)`.
- `GameClock` — current Tick, `Advance(int ticks)`, raising `OnTick`, `OnDayChanged`,
  `OnWeekChanged` through an internal event bus.

Entities (plain data + behaviour, no Unity types):
- `Agent` — Id, Name, Codename, ClassId, Level, Exp, Skills (Infiltration, Combat, Tech,
  Social, Nerve as a `SkillSet` struct of ints), PhysicalStamina, MentalStamina,
  Loyalty, TraitIds, Status enum (Idle, Training, Resting, Recovering, OnMission,
  Captured, Dead, Retired), AssignedRoomId, SalaryPerWeek, HiredOnTick, MissionsCompleted.
- `Room` — Id, TypeId, GridX, GridY, Width, Level, Condition(0-100),
  AssignedAgentIds, BuiltOnTick, mergeability helpers.
- `BaseLayout` — the grid: `CanPlace(typeId, x, y, width)`, `Place`, `Demolish`,
  `TryMergeAdjacent`, depth-based cost modifier, occupancy queries.
- `Resources` — Funds (long), Intel (int), Materials (int), Reputation (int), Heat (int),
  with explicit `TrySpend` returning false rather than going negative.
- `WorldState` — the single serializable root: Seed, Clock, Resources, BaseLayout,
  all Agents, Roster of recruits, active Missions, Contracts, Flags, RngStreams state.

Command/event layer (this is how Unity will talk to Core):
- `interface ICommand { CommandResult Validate(WorldState w); void Apply(WorldState w, IRng rng); }`
- `CommandResult` — Ok / Rejected(reasonCode, args) — reason codes are enum values that the
  UI maps to localized text; Core never produces English prose for the player.
- `GameSession` — owns WorldState, exposes `Execute(ICommand)`, `AdvanceTick()`,
  `IObservable`-style event stream of `GameEvent` records (AgentInjured, RoomBuilt,
  MissionCompleted, WeekSettled, ...). Keeps an ordered `CommandLog` for replay.

## Deliverables
1. All csproj files with correct TargetFrameworks; `dotnet build` passes.
2. xUnit tests proving: tick/day/week arithmetic; RNG reproducibility from the same seed;
   independent streams do not interfere; grid placement rejects overlaps and out-of-bounds;
   TrySpend never produces a negative balance.
3. `docs/ARCHITECTURE.md` with a diagram (plain text) of Core ↔ Presentation and a short
   explanation of the command/event flow.
4. Report the exact package versions you pinned.
````

---

## Stage 2 — Data tables

````text
Stage 2 — All tuneable content as CSV compiled by Luban. No magic numbers in Core after this.

Create `data/luban.conf`, `__tables__.csv`, `__beans__.csv`, `tools/gen.ps1`
(emitting C# into src/ProjectSpy.Tables/Gen and binary into a shared data folder that
both the console projects and Unity can load), and these tables with real seeded content:

- `agent_class.csv` — id, name_key, base skills, growth rate per skill, salary_base,
  recruit_cost_base, preferred_room_ids. 4 classes: Infiltrator, Operative(combat),
  Technician, Facilitator(social). Make the growth curves genuinely different.
- `agent_name.csv` — 80 first names, 60 surnames, 60 codenames, with a region tag.
- `trait.csv` — id, name_key, desc_key, rarity, is_hidden, conflicts_with,
  effect_type, effect_value. 30 traits: ~12 positive, ~12 negative, 6 hidden
  (including Mole and Deserter).
- `skill_curve.csv` — level, exp_required, stat_cap_bonus (levels 1-20).
- `room_type.csv` — id, name_key, category(Train/Recover/Ops/Support/Defense/Admin),
  build_cost, build_ticks, upkeep_per_week, width, max_level, worker_slots,
  trains_skill, train_rate_per_tick, effect_type, effect_value, required_act,
  min_depth, merge_group. ~22 rooms per the GDD.
- `room_upgrade.csv` — room_type_id, level, cost, upkeep_delta, effect_multiplier.
- `mission_type.csv` — id, name_key, objective_type(StealData/Sabotage/Assassinate/
  Rescue/PlantBug/Recon), tier, node_count_min, node_count_max, base_difficulty,
  base_reward_funds, base_reward_intel, heat_gain, duration_ticks_per_node,
  required_agents_min, required_agents_max, failure_penalty.
- `map_gen_rule.csv` — tier, layer_count, nodes_per_layer_min/max, branch_chance,
  dead_end_chance, security_room_ratio, loot_node_ratio, guaranteed_alt_routes.
- `node_room.csv` — id, name_key, tags, base_security, noise_modifier, allowed_tiers,
  weight, possible_event_ids, loot_table_id.
- `mission_event.csv` — id, name_key, trigger_tags, skill_checked, difficulty_class,
  alarm_on_success/partial/failure, physical_cost, mental_cost, loot_table_id,
  narrative_key. At least 45 events with real variety.
- `gadget.csv` — id, name_key, slot, craft_cost, craft_ticks, uses, effect_type,
  effect_value, required_room_level.
- `loot_table.csv` — id, entry_id, item_id, weight, min_count, max_count.
- `item.csv` — id, name_key, category, sell_value, stack_max.
- `heat_tier.csv` — threshold, raid_chance_per_week, enemy_strength, contract_quality_bonus.
- `contract_offer.csv` — tier, weight, client_faction, modifier_type, modifier_value.
- `localization/th.csv` and `localization/en.csv` — every name_key and desc_key used above,
  Thai written naturally, not machine-translated word order.

Rules:
- Every id space is non-overlapping and documented in `data/README.md`.
- gen.ps1 fails loudly with download instructions if Luban.dll is missing.
- Add a `TableValidator` in Core.Tests that loads the generated tables and asserts:
  no dangling foreign key, no weight <= 0, every name_key exists in both localization files,
  every skill referenced by a room exists, every mission tier has at least 3 usable node rooms.
  This test must fail the build when a designer makes a bad edit.
````

---

## Stage 3 — Simulation core: agents, rooms, economy

````text
Stage 3 — The living base. Everything here is in ProjectSpy.Core with xUnit coverage.

1. Tick processing pipeline, executed in this fixed order every tick:
   RoomConstruction -> Training -> Recovery -> MissionProgress -> EventChecks -> StatusDecay.
   Order is part of the save contract; document it and assert it in a test.

2. Training system:
   - An agent assigned to a training room gains `train_rate_per_tick * roomLevelMultiplier *
     classAffinity * traitModifier` exp toward one skill, consuming Physical or Mental stamina
     depending on the room.
   - Diminishing returns as the skill approaches the level cap from `skill_curve.csv`.
   - Training with stamina below 20 produces a fraction of the gain and drains Loyalty —
     overworking must be a real, visible mistake.

3. Recovery system:
   - Dorm restores Physical, Lounge/Therapy restores Mental (Mental recovers roughly a third
     as fast as Physical by design — make that ratio a table value, not a constant).
   - Infirmary clears injuries over N ticks based on severity.
   - Burnout state when Mental hits 0: the agent refuses assignment, loses Loyalty each day,
     and recovers only with dedicated rest. Burnout must be recoverable but expensive.

4. Economy:
   - Weekly settlement: salaries + room upkeep + loan interest, deducted on day 7 of each week.
   - Bankruptcy: funds below zero triggers a 7-day grace period with escalating penalties
     (Loyalty drain, then forced resignations, then game over). Never instantly lose.
   - Loans: amount, weekly interest, from `contract_offer`-adjacent table if you need one.

5. Recruitment:
   - A refreshing pool of candidates, size and quality gated by HR room level and Reputation.
   - Candidate generation: pick class, roll skills around class baseline with variance,
     roll 1-3 traits respecting `conflicts_with`, hidden traits are stored but flagged unseen.
   - Hiring costs an up-front fee and commits to a weekly salary.

6. Loyalty & Morale:
   - Daily drift from: workload, pay versus market rate, losses on missions, idle time,
     roommate traits, facility quality.
   - Thresholds trigger events: complaints, demands for a raise, resignation notice, defection.
   - The UI only ever sees a coarse band (Devoted / Content / Uneasy / Resentful), never the number.

7. Mole system:
   - A hidden-trait agent silently adds Heat each week and can leak a mission in advance,
     raising its difficulty.
   - The Counter-Intel room runs investigations over time producing probabilistic evidence;
     accusing the wrong agent costs organisation-wide Loyalty.
   - Make the design falsifiable: a player paying attention to Heat spikes versus mission
     logs should be able to narrow it down. Document that reasoning chain in docs/.

Tests to deliver: a 365-day headless run with a fixed seed that asserts no exception,
no negative resource, no stuck agent status, and a stable final state hash; plus targeted
tests for each system above; plus a test proving two sessions with the same seed and the
same command log produce byte-identical WorldState.
````

---

## Stage 4 — Mission generation + fog of war

````text
Stage 4 — The heart of the game. Take this one slowly and test it heavily.

1. `MissionMapGenerator` (Core, deterministic, driven by map_gen_rule.csv):
   - Layered graph: Entry layer -> N transit layers -> Objective -> Extraction.
   - Guarantees: at least one path Entry->Objective->Extraction; at least `guaranteed_alt_routes`
     distinct routes; no orphan node; dead ends exist but are reachable and may hold loot.
   - Node assignment from node_room.csv weighted by tier, with the constraint that at least
     one Security node sits on every route to the Objective.
   - Expose a deterministic `MapSeed` derived from WorldSeed + missionId so the same mission
     always regenerates identically on load.

2. `FogOfWar` (Core):
   - Node visibility states: Hidden, Silhouette, Scouted, Revealed.
   - Reveal radii come from the team's effective Infiltration — use the BEST infiltrator in the
     team, plus a smaller bonus from the team average, so bringing a specialist matters.
   - Radii formula and its constants live in a table, not in code.
   - Gadgets and hacked terminals can upgrade specific nodes to Scouted.
   - CRITICAL: Hidden node contents must not be computed until first observed, OR must be
     computed but never exposed through any Core API that the UI can reach. Prefer lazy
     generation keyed by MapSeed so the data genuinely does not exist yet.
     Write a test that asserts the public API surface cannot enumerate hidden node contents.

3. `MissionRunner` (Core):
   - Per tick: move the team along the chosen edge, or resolve the current node.
   - Node resolution: pick an event from mission_event.csv filtered by node tags, run the
     skill check d100 + stat + gadget + team support versus difficulty class,
     map to Critical/Success/Partial/Failure, apply alarm, stamina, injury and loot effects.
   - Alarm meter 0-100 with the five bands from the GDD, each band applying concrete effects
     (difficulty modifier, edge closure, pursuit spawning, forced extraction at 100).
   - Team splitting: the player may split into sub-teams; each resolves independently and
     they may regroup. Keep this in the model from the start — retrofitting it later is painful.
   - Player commands mid-mission: Advance(edge), Hold(reduce alarm, costs ticks),
     UseGadget, Split, Regroup, ForceDoor, Abort. Each is an ICommand validated against
     the current mission state.
   - Outcome: ObjectiveComplete / PartialSuccess / Aborted / Failed / Burned, each with a
     different reward and consequence profile including capture rolls for agents who were
     not at Extraction when the mission ended.

4. `MissionReport` — a structured, replayable log of every node entered, roll made,
   modifier applied and consequence. This is both the player-facing after-action report and
   the developer debugging tool. Keep the raw rolls in it behind a debug flag.

5. Capture and rescue: captured agents enter a countdown; the enemy extracts intel
   (Heat rises each day they are held); a Rescue mission generated against a specific site
   can recover them; letting the timer expire loses them permanently.

Tests: generate 10,000 maps across all tiers and assert connectivity, route-count and
security-coverage invariants on every one; run 1,000 full missions headless and assert no
exception, alarm stays in range, every mission terminates within a tick bound, and outcome
distribution is sane; prove fog state never leaks unobserved content.
````

---

## Stage 5 — Save/load, replay, and determinism guarantees

````text
Stage 5 — Make saving bulletproof before any UI depends on it. Save bugs destroy reviews.

1. `ProjectSpy.Core/Persistence/`:
   - `SaveFile` = Header + WorldState. Header: schema version, game build, world seed,
     playtime ticks, real-world timestamp, agency name, thumbnail bytes (optional),
     a content hash and a checksum over the payload.
   - MessagePack with explicit `[Key(n)]` on every member — never Contractless,
     never rely on member order.
   - Compress the payload (LZ4 block via MessagePack's built-in option) and verify the
     checksum on load before deserializing further.

2. Migration framework:
   - `ISaveMigration { int FromVersion { get; } SaveFile Apply(SaveFile s); }` applied in order.
   - Write migration v1->v2 now as a no-op example plus its test, so the pattern exists
     before it is urgently needed.
   - Loading a save newer than the build refuses politely; loading older runs migrations and
     reports what changed.

3. Data-table drift handling:
   - On load, any id in the save that no longer exists in the tables is quarantined into a
     `SaveIntegrityReport` with its owner, rather than crashing or being silently dropped.
   - The report is surfaced to the player as "this save was made with different content".

4. Slots:
   - Autosave each in-game day into a 5-slot rotation; manual save slots; one ironman slot
     that overwrites itself and refuses to be loaded after a run ends.
   - Atomic writes: write to .tmp, fsync, then move over the target. Never corrupt the old
     save by crashing mid-write. Add a test that simulates a crash between write and move.

5. Replay:
   - `ReplayFile` = seed + ordered command log + a state hash every 1000 ticks.
   - `ReplayVerifier` re-runs the log and asserts every checkpoint hash matches,
     reporting the first divergent tick and command when it does not.
   - Add a CI test that plays 2,000 scripted commands and verifies the replay.
   - This is the tool that will find every determinism bug for the rest of the project —
     make its failure output genuinely useful.

6. Determinism audit:
   - Grep the whole of ProjectSpy.Core for DateTime, System.Random, UnityEngine,
     Guid.NewGuid, File., Directory., Environment., and float arithmetic in rule paths.
     Paste the results. Fix anything found, then add a Roslyn analyzer or a unit test that
     reflects over the Core assembly and fails if a forbidden type is referenced.

Deliver `docs/SAVE-FORMAT.md` documenting the layout, the versioning policy and the
migration procedure.
````

---

## Stage 6 — Headless playability + balance harness (ก่อนแตะ Unity)

````text
Stage 6 — Prove the game is a game before spending a single hour on art or UI.

1. `src/ProjectSpy.Sim/` — a console app that can:
   - `play` — an interactive text-mode client: print base status, roster, contracts, missions;
     accept typed commands mapping 1:1 to ICommand; advance ticks. I must be able to play a
     full hour of the game in a terminal. If it is not fun here, no amount of art will save it.
   - `sim` — run N autonomous agencies for M days with a scripted AI policy, in parallel,
     and emit CSV + a markdown report.
   - `verify` — run the replay verifier over a directory of replay files.

2. AI policies for `sim`, selectable: Greedy (always take the highest-paying contract),
   Cautious (never deploy below 70% stamina), Balanced, Reckless, Idle (does nothing —
   the control group that must slowly go bankrupt).

3. Balance report contents, written to docs/BALANCE-<date>.md:
   - Funds over time per policy, with the bankruptcy day if any.
   - Mission success/partial/fail/burn rates by tier and by team composition.
   - Agent churn: deaths, captures, burnouts, resignations per 100 days.
   - Skill progression curves: ticks to reach Infiltration 50 / 75 / 90.
   - Heat trajectory and raid frequency.
   - Fog coverage: average fraction of a map visible at each Infiltration band — this must
     climb smoothly, since it is the core progression feeling.
   - Flagged as warnings: any tier whose success rate is above 90% or below 25%;
     any room never worth building under any policy; any trait with no measurable effect;
     any dominant strategy that beats all others by more than 2x.

4. Run it at 100, 300 and 500 days across all policies. Present the findings and a proposed
   CSV diff, but DO NOT edit the data tables until I approve the diff.

5. `docs/DESIGN-REVIEW.md` — your own honest critique of the simulation as a game:
   where is the tension, where is the boredom, which decisions are actually meaningful and
   which are obviously-correct non-choices. Be blunt. I would rather fix it now.
````

---

## Stage 7 — Unity bootstrap + editor automation

````text
Stage 7 — Bring up the Unity project around the finished Core.

1. Create `UnityProject` (Unity 2022.3 LTS, URP 2D) with Packages/manifest.json
   referencing VContainer, UniTask, R3, and Newtonsoft Json. Add the Steamworks.NET
   package reference but do not integrate it yet (stage 10).

2. `tools/sync-dlls.ps1` — publish ProjectSpy.Core and ProjectSpy.Tables as
   netstandard2.1 and copy the DLLs plus their non-conflicting dependencies into
   `Assets/Plugins/ProjectSpy/`. Skip anything Unity already ships. Print a clear
   warning list of version conflicts rather than failing silently.

3. Assembly definitions: `ProjectSpy.Unity` (runtime) and `ProjectSpy.Unity.Editor`.
   The runtime asmdef must NOT be allowed to compile if someone adds a game rule to it —
   add an editor-time check that flags Presentation types containing arithmetic on
   Core stat fields, or at minimum document the rule prominently.

4. `Assets/Main/Editor/` automation, all reachable from a `ProjectSpy/Setup` menu:
   - `PlaceholderArtGenerator` — generate flat-colour PNGs with correct importer settings
     (Point filter, no compression, PPU 16): agent sheet 16x32 with 5 states x 4 frames,
     room module tiles 96x64 per room_type id, item icons 16x16 per item id,
     node icons 32x32 per node_room id, 9-slice UI panels.
   - `PrefabGenerator` — AgentView, RoomView, RoomSlotView, MissionNodeView, MissionEdgeView,
     plus every UI prefab stage 8 will need.
   - `SceneGenerator` — three scenes: `Boot` (index 0, loads Core, tables, save system,
     then routes), `Base` (the cutaway grid), `Mission` (the node map). Base and Mission are
     loaded additively over a persistent root; Boot is never returned to.
   - `ProjectValidator` — scenes in build settings, one LifetimeScope per scene,
     no null serialized reference, every table id has matching placeholder art.
     Runs from the menu and from batch mode with a non-zero exit code on failure.
   - `BatchSetup.RunFullSetup()` callable via -executeMethod for CI.

5. Runtime infrastructure:
   - `RootLifetimeScope` registering: `GameSession` wrapper, `TableService`,
     `SaveService`, `SceneRouter`, `TimeController`, `IWindowService`, `IToastService`,
     `AudioService`, `LocalizationService`, `InputService`.
   - `GameSessionRunner : ITickable` — converts real seconds into Core ticks according to the
     current speed setting, calls `session.AdvanceTick()`, and republishes Core `GameEvent`s
     onto an R3 `Subject` the UI subscribes to. Pausing stops calling AdvanceTick; it must
     never change what AdvanceTick does.
   - `SceneRouter` — additive load the new scene, then unload the previous, then set active.
     Never leave zero gameplay scenes loaded.

6. Update `UnityProject/SETUP.md` so the remaining manual steps are at most three items.
````

---

## Stage 8 — UI framework + Base scene

````text
Stage 8 — This game IS its UI. Budget accordingly.

1. UI foundation:
   - `UIRoot` with layers World / HUD / Window / Modal / Toast / Tooltip, each a child canvas.
   - `WindowService` — stack-based, pooled, `UniTask<TResult> OpenAsync<TWindow,TParam,TResult>`,
     Escape closes the top, modal windows raycast-block beneath, and opening a window
     optionally auto-pauses the simulation (user setting).
   - `TooltipService` — hover any stat, trait, room or item to get a rich tooltip assembled
     from the tables plus the live modifier breakdown ("Infiltration 62 = base 50 +8 training
     +10 Ghost trait -6 fatigue"). This breakdown is non-negotiable: a management game that
     hides its maths is a frustrating one.
   - `ToastService` — info/success/warning/error plus a persistent pending indicator.
   - All binding via R3 observables from the Core event stream. No Update() polling of Core.

2. Top bar: date and weekday, pause/1x/2x/4x with keyboard space and 1/2/3,
   Funds (with weekly net delta shown in green or red), Intel, Materials, Reputation,
   and a Heat gauge that visibly changes mood as it climbs.

3. Base grid view:
   - Render the cutaway from BaseLayout: rooms, merged rooms, construction-in-progress,
     damaged rooms, and the agents inside them as small animated sprites doing their job.
   - Build mode: a room catalogue filtered by unlocked act, ghost preview with validity
     colouring, cost and upkeep preview, depth cost modifier shown, and a confirm step.
   - Click a room: detail panel with level, condition, assigned agents, output rates,
     upgrade and demolish actions.
   - Drag an agent from the roster onto a room to assign; invalid targets explain why.

4. Roster panel (left side, modelled on the Idol Manager layout):
   - Sortable, filterable list of agents with portrait, codename, class, status,
     Physical and Mental stamina bars, and the five skills.
   - Agent detail window: full stats, visible traits, known history, mission record,
     salary, Loyalty band with a plain-language mood line, and a timeline of notable events
     in their career. This window is where the player forms attachments — make it good.

5. Contracts window: available contracts with client, objective type, tier, estimated
   difficulty versus your roster, reward, heat gain, expiry countdown, and a prepare-mission
   flow (select agents, assign gadgets, see a readiness estimate, confirm dispatch).
   The readiness estimate must be an honest derived number from Core, never a fudge.

6. Localization wired throughout, Thai as the default, with a language toggle in settings.
   Missing keys render as the key plus a console warning, never as an exception.

Deliver EditMode tests for the pure presenter logic: affordability gating, placement validity,
sort and filter correctness, readiness estimate maths.
````

---

## Stage 9 — Mission scene + game feel

````text
Stage 9 — Make infiltration tense. This is where the game earns its screenshots.

1. Mission map rendering:
   - Node graph laid out left to right by layer with a deterministic, readable layout
     (same map = same layout every time, including after reload).
   - Visual states for Hidden (not drawn at all, just dark space), Silhouette (an outline with
     a question mark), Scouted (room icon plus a security pip row), Revealed (full detail
     plus any loot or guard markers), and Cleared.
   - Edges drawn only between nodes the player has at least silhouetted; locked and closed
     edges shown distinctly.
   - Fog as an actual rendered darkness with a soft falloff around the team, not a flat
     grey overlay — this is the single most important visual in the game.

2. Team and movement:
   - Agent tokens moving along edges with tick-synced animation that still looks right at 4x.
   - Sub-team support in the UI from day one: select a token or a group, issue orders to it.
   - Per-agent floating state: stamina pips, injury icon, panic icon, gadget-ready icon.

3. Alarm presentation:
   - A prominent meter with the five named bands, colour and audio shifting as it climbs.
   - Band transitions are a full-screen moment: screen edge vignette, a stinger sound,
     a one-line message ("Security sweep initiated — east corridor sealed").
   - At Burned, the UI must make the stakes unmistakable without blocking the player's
     ability to act.

4. Command bar: Advance, Hold, Use Gadget, Split, Regroup, Force Door, Abort,
   each showing its tick cost and its risk in the tooltip. Keyboard shortcuts for all.

5. Node resolution feedback:
   - A roll popup that shows the check being made, the modifier breakdown and the result
     band, with an option in settings to show or hide the raw numbers for players who
     prefer narrative framing.
   - Four distinct result presentations so Critical and Partial never feel the same.

6. After-action report: route taken, every event with its outcome, loot gained, exp per agent,
   injuries, alarm history as a sparkline, and the narrative summary lines. Exportable to
   clipboard as text — players share these, and so will your bug reporters.

7. AudioService with pooled sources and categories; placeholder clips generated by an editor
   script so nothing is null. Register: footsteps, door, hack, alarm stingers per band,
   combat, injury, loot, extraction success, mission failure, and an ambient bed that
   layers in tension as alarm rises.

8. Camera: pan and zoom with edge-scroll and middle-drag, clamped to the map bounds,
   auto-focus on the acting sub-team, and a toggle to disable auto-focus for players who
   want manual control.
````

---

## Stage 10 — Meta, Steam, polish, release readiness

````text
Stage 10 — Everything between "it works" and "it ships".

1. Campaign and narrative layer:
   - `StoryDirector` in Core: act progression driven by flags, days elapsed and milestones,
     unlocking room types, mission tiers and antagonists.
   - Event system: dilemma events with 2-4 choices and real consequences, defined in
     `story_event.csv` with conditions, weights and cooldowns. At least 40 events,
     written in Thai first, with English as a parallel column.
   - Three endings gated on Reputation, Heat, Intel and surviving-roster state,
     plus an epilogue summarising what became of every agent who served — living or not.
     That epilogue is the emotional payoff of the permadeath design; do not cut it.
   - Endless mode unlocked after any ending, with escalating difficulty and a local
     high-score table.

2. Difficulty and accessibility:
   - Difficulty presets altering economy pressure, enemy strength, capture and death chance,
     plus a custom mode exposing those sliders directly.
   - Ironman toggle (single rotating save, no reload after a bad outcome).
   - Accessibility: UI scale, colourblind-safe alarm palette, full keyboard navigation,
     screen-shake and flash toggles, adjustable text speed, and a dyslexia-friendly font option.

3. Steam integration via Steamworks.NET, wrapped behind an `IPlatformService` so the game
   still runs with no Steam client present:
   - Achievements (design 30, spanning early, mid, late and "interesting failure" categories).
   - Steam Cloud save sync with a conflict resolution dialog.
   - Rich presence showing act, day and agency name.
   - A `docs/STEAM-CHECKLIST.md` covering store page assets, capsule sizes, build branches,
     depot layout and the demo build plan.

4. Performance and stability:
   - Profile a 500-day save: target 60fps on a mid-range laptop, zero per-frame managed
     allocation in the base and mission update paths, and a sub-2-second save and load.
   - Object pooling for agent tokens, UI rows and toasts.
   - Global exception handler that writes a crash report with the last 200 log lines,
     the current save and the replay log, and shows the player a dialog explaining how to
     send it to me.

5. Final hardening:
   - Run the fuzzer: throw random and malformed commands at Core and assert the world is
     either unchanged or legally changed, never corrupted.
   - Load every save file produced by every earlier stage and confirm migrations work.
   - Re-run the balance simulator and update docs/BALANCE.md with final numbers.
   - `/review` the whole repository against @knowledge.md and report findings in a severity
     table, then fix everything high and medium.

6. Release package:
   - `docs/RELEASE-CHECKLIST.md`, `docs/KNOWN-ISSUES.md`, a bug report template,
     a Thai and English player guide, and a playtest feedback form.
   - Tag v0.9.0-beta and write an honest summary of what is still placeholder,
     what is stubbed, and what a playtester might mistake for a finished feature.
````

---

## หมายเหตุสำคัญ 3 ข้อ

**1. Stage 6 คือจุดตัดสินชะตาโปรเจกต์** — ถ้าเล่นใน terminal แล้วไม่สนุก อย่าเพิ่งไป Stage 7 ให้วนกลับไปแก้ Core ก่อน เพราะการทำ UI สวยๆ ครอบเกมที่ไม่สนุกคือการเผาเวลา 3 เดือน

**2. Stage 4 ควรใช้เวลามากที่สุด** — mission + fog คือสิ่งที่คนซื้อเกม ถ้าจำเป็นต้องแบ่ง Stage 4 ออกเป็น 4a (map gen) กับ 4b (resolution + alarm) ก็ควรทำ

**3. ลำดับที่ห้ามสลับ:** 1 → 2 → 3 → 4 → 5 → 6 ส่วน 7-10 ค่อนข้างอิสระ แต่ 8 ต้องมาก่อน 9

ให้ผมรวม GDD + knowledge.md + Stage 1-10 ทั้งหมดนี้เป็นไฟล์ Word ไว้เปิดคู่กับ terminal ไหมครับ หรืออยากให้ผมแตก **Stage 4 เป็น 4a/4b แบบละเอียด** ก่อน เพราะเป็นส่วนที่เสี่ยงพังมากที่สุดครับ?