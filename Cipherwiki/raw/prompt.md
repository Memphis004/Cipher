
---

# 0. อัปเดต knowledge.md (ยิงอันนี้ก่อนเสมอ)

````text
Replace the "Presentation target", "Time model", and "Mission structure" sections of
@knowledge.md with the following, and append the new sections. Keep everything else intact.

## Presentation target
- 2.5D: 3D meshes viewed through a fixed side-on cutaway camera. The player never rotates it.
- Base view reference: Lobotomy Corporation. Tactical view reference: Into the Dead - Our Darkest Days.
- Unity uses URP (3D), NOT URP 2D.
- Lighting is gameplay. Core is always the authority on "can X perceive Y";
  Unity only renders what that computation already decided.

## Two time scales
- STRATEGIC TICK = 1 in-game hour, 24 per day. Drives the base: construction, training,
  recovery, economy, weekly settlement, sleeper-agent progress, travel.
  Speeds: Pause / 1x / 4x / 16x / 64x. At high speed days and weeks pass quickly.
- TACTICAL STEP = 100 ms, 10 per simulated second. Active ONLY inside a tactical mission.
  Speeds: Pause / 0.5x / 1x / 2x / 4x. One real minute of play is roughly one in-game minute.
- The base clock does NOT advance while a tactical mission is running. On mission end,
  elapsed steps convert to strategic ticks once: ticks = ceil(steps / 36000).
- Speed controls at either scale only change how real seconds map to steps/ticks.
  They must NEVER change simulation results.

## Direct control and determinism
- The player directly controls agents during tactical missions, switching between them.
- Input is sampled ONLY at tactical step boundaries and converted into ICommands appended
  to the command log. No gameplay logic may read Unity Input or Time.deltaTime.
- Rendering interpolates between steps; the simulation never does.
- Replay must therefore still reproduce a tactical mission step-for-step from seed + log.

## Tactical space model
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

## Mission structure
- A mission is a generated CONTINUOUS BUILDING, not an abstract graph of abstract nodes.
  Rooms are walkable spaces on floors; connections are real doors, stairs, vents and windows.
- Pre-mission intel comes from a Sleeper Agent (see below). In-mission perception comes from
  the agents themselves.
- Infiltration skill governs PERCEPTION RANGE and how much of an adjacent space an agent can
  read before committing to entering it. It no longer reveals abstract map nodes.

## Sleeper Agent and pre-mission intel
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

## Tactical squad
- A mission deploys 3-5 agents. One is controlled directly at any moment; the others follow
  their assigned ROLE behaviour until the player switches to them or issues an order.
- Some missions provide a FORWARD COMMAND POST (a van or an adjacent building) that is a real
  location on the map, grants support abilities, and can itself be compromised.

## Lethality is a tuning decision, not a code decision
- Killing is permitted mechanically, but every lethal act carries data-driven costs:
  heat_cost, evidence_level, mental_cost, body-discovery consequences.
- Whether the final game encourages or forbids killing is decided later by tuning those
  values. No code path may assume one answer.

## NPC intelligence
- Site NPCs run GOAP (Goal-Oriented Action Planning) in Core, fully deterministic.
- Planning cost and replanning frequency must be budgeted; see the tactical stages.
````

---

# 1. Patch Stage 1 — Fixed-point + dual clock

````text
Patch Stage 1 to support the updated @knowledge.md. Do not rewrite what already works.

1. `Fixed32` readonly struct in ProjectSpy.Core.Math:
   - Backed by `int`, 1 unit = 1 centimetre. Operators + - * / with correct rounding,
     comparison, `Abs`, `Min`, `Max`, `Clamp`, `FromCm(int)`, `FromMetres(int)`,
     `ToDisplayMetres()` returning a float FOR PRESENTATION ONLY (mark it with a comment).
   - Deterministic `SqrtApprox` and a fixed-point `Lerp`.
   - Full xUnit coverage including overflow behaviour at the extremes.

2. Split the clock:
   - `StrategicClock` — the existing Tick model (1 hour, 24/day, 7-day week), unchanged.
   - `TacticalClock` — `Step` (long), 10 steps per simulated second, with
     `Second`, `Minute`, and `Step.FromSeconds(int)`.
   - `TimeScale` enum per scale, with the speed sets from knowledge.md.
   - `GameSession` gains a `SessionMode` of Strategic or Tactical. While Tactical,
     `AdvanceStrategicTick` must throw — this invariant is a test.
   - `MissionTimeConverter.StepsToStrategicTicks(long steps)` = ceil(steps / 36000),
     applied exactly once when a mission closes. Test it at the boundaries.

3. Extend `RngStreams` with new independent streams: Tactical, Goap, Sleeper, Loot.
   Add a test proving that adding a roll to one stream does not shift any other.

4. Extend `WorldState` with: `ActiveMission` (nullable tactical state),
   `SleeperOperations` (list), and `IntelSnapshots` (by siteId).

5. Update docs/ARCHITECTURE.md with the two-scale time diagram and the rule that the base
   clock freezes during tactical play.
````

---

# 2. Patch Stage 2 — ตารางใหม่

````text
Patch Stage 2: add the tables the tactical layer needs. Keep all existing tables.

New CSVs under data/, each with real seeded content and entries in __tables__.csv:

- `site_template.csv` — id, name_key, tier, floor_count_min/max, width_per_floor_min/max
  (in lane units, 1 unit = 1 metre), security_grade, guard_count_min/max, civilian_count,
  objective_room_tags, allowed_room_ids, has_forward_post, lighting_profile_id.
- `room_template.csv` — id, name_key, tags, width_min, width_max, occluder_density,
  default_light_level, noise_absorption, valid_floors(ground/upper/basement/any),
  door_positions_rule, prop_set_id, loot_table_id, weight_by_tier.
- `connection_type.csv` — id (Door, LockedDoor, Stair, Ladder, Vent, Window, Hole),
  traverse_steps, noise_on_use, requires_skill, skill_dc, blocks_vision, can_be_locked,
  can_be_barricaded, usable_by_npc.
- `guard_archetype.csv` — id, name_key, vision_range, vision_cone_degrees, hearing_range,
  patrol_speed, alert_speed, suspicion_gain_rate, suspicion_decay_rate, combat_skill,
  health, goap_goal_set_id, carries_radio, carries_key_id.
- `goap_action.csv` — id, name_key, preconditions, effects, base_cost, duration_steps,
  interrupt_priority, required_archetype_tags.
- `goap_goal.csv` — id, name_key, priority_curve, satisfaction_condition, valid_archetypes.
- `light_source.csv` — id, name_key, radius, intensity_level(Lit/Dim), can_be_destroyed,
  can_be_switched, noise_on_destroy, restores_after_steps.
- `noise_profile.csv` — action_id, base_radius, attenuation_per_connection,
  attenuation_per_floor, suspicion_weight.
- `agent_role.csv` — id, name_key, auto_behaviour_set, allowed_orders, passive_bonus.
  At minimum: PointMan, Hacker, Overwatch, Mule, Medic, Handler(in command post).
- `tactical_action.csv` — id, name_key, category(Move/Interact/Combat/Stealth/Support),
  steps_cost, stamina_cost, mental_cost, noise_profile_id, skill_used, base_dc,
  is_lethal, heat_cost, evidence_level, mental_cost_on_witness.
- `throwable.csv` and `melee_weapon.csv` — range, arc, damage, noise, is_lethal, uses.
- `sleeper_op.csv` — site tier, ticks_per_intel_percent, base_discovery_chance_per_tick,
  infiltration_modifier, social_modifier, poison_chance_on_discovery, heat_on_discovery.
- `capture_site.csv` — holding room tags by site tier, guard density, rescue time limit days,
  intel extracted per day held.

Extend `TableValidator` with the new invariants: every GOAP action's preconditions reference
declared world-state keys; every guard archetype's goal set exists; every room template has at
least one valid connection rule; every tactical action referencing a skill uses a real skill;
every site template can actually be generated (no impossible constraint combination).
Add Thai and English localization rows for every new name_key.
````

---

# 3. Stage 4a — Building generator

````text
Stage 4a — Generate a believable, playable building. Deterministic, Core-only, no Unity.

`SiteGenerator` in ProjectSpy.Core.Missions:

1. Input: siteTemplateId, tier, a MapSeed derived from WorldSeed + missionId.
   Output: a `SiteLayout` consisting of Floors, each with an ordered list of Rooms occupying
   non-overlapping intervals [startX, endX) in lane units, plus a Connection list.

2. Generation order, each step deterministic:
   - Choose floor count and per-floor width.
   - Partition each floor into rooms from room_template.csv respecting width ranges,
     valid_floors and weight_by_tier.
   - Place horizontal connections between adjacent rooms per door_positions_rule.
   - Place vertical connections (stairs, ladders, vents) so every floor is reachable from the
     entrance, then add optional extras per the branch rules.
   - Place the entrance, the objective room, at least one alternate route to the objective,
     and one or more extraction points (which may differ from the entrance).
   - Place light sources, occluders, loot containers and interactables.
   - Place guards with patrol routes, and civilians where the template calls for them.
   - If a forward command post is enabled, place it as a separate small map region connected
     to the main site by a travel edge with a step cost.

3. Hard invariants, asserted in generation and in tests:
   - Every room is reachable from the entrance.
   - At least two routes reach the objective that do not share every connection.
   - Every floor with a room has at least one vertical connection.
   - No room is narrower than its template minimum, no overlapping intervals.
   - Every patrol route is traversable by its guard archetype.
   - At least one extraction point is reachable from the objective.

4. Lazy content: a room's detailed contents (exact loot roll, exact prop layout) are generated
   on first observation from MapSeed + roomId, never up front. Add a test proving that the
   public API cannot enumerate unobserved room contents.

5. `SiteLayout` must be fully serializable and must regenerate identically from MapSeed after
   a save/load round trip. Test it.

6. Deliver a `render-ascii` debug dump that prints the building floor by floor as text, with
   room names, widths, connections, guards and lights. I will use this constantly in stage 6.

Tests: generate 20,000 sites across all templates and tiers; assert every invariant on each;
report the distribution of floor counts, room counts, route counts and generation time.
Generation of one site must take under 10 ms.
````

---

# 4. Stage 4b — Sleeper Agent + intel

````text
Stage 4b — Pre-mission intelligence. This is where fog of war is bought and sold.

1. `SleeperOperation` in Core (strategic scale):
   - Fields: agentId, siteId, startedTick, intelPercent (0-100), status
     (Inserting, Embedded, Discovered, Burned, Extracted, Poisoned), discoveredOnTick.
   - Per strategic tick: accrue intel per sleeper_op.csv modified by the agent's Infiltration
     and Social; roll discovery against base_discovery_chance modified by the same stats and
     by site security.
   - On discovery: raise Heat, mark the agent Burned, and with poison_chance the operation
     becomes Poisoned — the player is NOT told. A poisoned op keeps reporting intel, but the
     generated IntelSnapshot is deliberately wrong in specific, survivable ways
     (a patrol route that no longer exists, a door marked unlocked that is locked,
     an objective in the wrong room). Never make poisoned intel unwinnable, only costly.
   - The player may recall a sleeper at any time, keeping the intel accrued so far.

2. `IntelSnapshot`:
   - Produced from the real SiteLayout, filtered by intelPercent per the bands in knowledge.md,
     stamped with the tick it was taken.
   - Every piece of information carries a `Confidence` of Observed, Reported or Stale,
     and a source. The tactical layer later overwrites Reported/Stale entries with Observed
     facts as agents perceive them, and raises a `IntelContradicted` event when they disagree —
     that event is a gameplay moment, not an error.
   - At 100% the snapshot additionally includes the holding room of any captured agent at
     that site, and is refreshed every tick until the mission starts.

3. `FogState` per room and per connection, derived at mission start from the IntelSnapshot:
   Unknown, Reported(stale allowed), Scouted, Observed, Cleared. The tactical sim promotes
   these as agents perceive. Demotion never happens, but a Reported fact can be contradicted.

4. Strategic UI contract (data only at this stage): expose everything the base-side screen
   will need — per-site known intel percent, elapsed and estimated remaining ticks,
   current discovery risk per tick, and a preview of what the next intel band will unlock.

5. Rescue interaction: if a captured agent is held at a site, running a sleeper op there to
   100% reveals the holding room. Below 100%, a rescue mission must search for them, and the
   capture countdown keeps running during the search.

Tests: intel band filtering never leaks a fact above the current band; poisoned snapshots are
always still solvable (assert a valid route to the objective exists using only poisoned data);
a 100% snapshot matches the live layout exactly; discovery probability over 10,000 simulated
operations matches the table within tolerance.
````

---

# 5. Stage 4c — Tactical simulation core

````text
Stage 4c — The tactical simulation. Core only, no Unity, fully deterministic, fixed-point.
This is the single most important stage in the project. Take it seriously and test it hard.

1. `TacticalState`: SiteLayout, entities (agents, guards, civilians), their TacticalPositions,
   postures, stamina, noise events in flight, light state, door states, alarm level,
   objective progress, and the current step count.

2. Movement:
   - Postures: Prone, Crouch, Walk, Run, each with speed, noise and visibility multipliers.
   - Movement is along a floor's line; crossing a connection takes traverse_steps and
     produces noise per connection_type.csv.
   - Pathfinding over the room/connection graph with a fixed-point cost function; the result
     must be stable for identical inputs (break ties by id, never by hash order).
   - Blocked, locked and barricaded connections are handled explicitly with their own
     interaction actions.

3. Perception (the heart of stealth):
   - `PerceptionSystem.CanPerceive(observer, target)` returns a Perception of
     None, Noticed, Identified, with the full modifier breakdown attached so the UI can
     explain it. This breakdown is mandatory.
   - Inputs: distance on the line, facing and cone, occluders between, target light level,
     target posture, target movement, observer's vision stats, and any disguise.
   - Light: emitters define lit intervals; destroying or switching a light changes them;
     Core tracks Lit/Dim/Dark per interval.
   - A `SuspicionMeter` per NPC, rising with partial perceptions and decaying over time,
     crossing thresholds that drive the GOAP goals in stage 4d.

4. Noise:
   - `NoiseEvent { origin, radius, profileId, step }` propagates to listeners by range,
     attenuated per connection and per floor.
   - Noise is the primary way a careless player loses, so it must be legible: every noise
     event is recorded in the mission log with its source and who heard it.

5. Actions: implement every row of tactical_action.csv — move, open/close/lock/pick/force,
   hack terminal, hide body, drag body, search container, plant device, use throwable,
   melee attack, non-lethal takedown, lethal takedown, heal, revive, carry downed ally,
   disguise, switch light, use vent, signal squad.
   Each action validates, consumes steps and stamina, emits noise and applies effects.
   Lethal actions apply heat_cost, evidence_level and mental_cost from the table — never
   hard-code a moral stance.

6. Alarm: the site-wide alarm level with the five bands from the GDD, driven by NPC awareness
   rather than by an abstract counter. Bands change guard behaviour, lock connections,
   spawn responders and eventually force extraction.

7. Damage, downing, death and capture:
   - Health and injury per agent; being downed starts a bleed-out timer.
   - Allies may stabilise, carry or drag a downed agent.
   - A downed agent left behind when the mission ends is CAPTURED, not killed, unless the
     damage was lethal. Captured agents are moved to a holding site with an unknown location
     unless a 100% sleeper op reveals it.
   - Death is permanent and must be rare enough to hurt and common enough to matter.

8. `TacticalMissionRunner.Step()` executes one 100 ms step in a documented fixed order:
   ApplyQueuedCommands -> MovementResolution -> ActionProgress -> NoisePropagation ->
   Perception -> NpcPlanning -> AlarmUpdate -> DamageAndStatus -> ObjectiveCheck -> EventEmit.
   This order is part of the save and replay contract; assert it in a test.

Tests: 5,000 headless missions with scripted policies asserting termination, no invalid state,
alarm in range, no entity off-map; a determinism test running the same seed and command log
twice and comparing a full state hash every 100 steps; a perception test matrix covering
light, posture, distance, occlusion and cone; a noise attenuation test matrix.
````

---

# 6. Stage 4d — GOAP

````text
Stage 4d — GOAP for site NPCs, in Core, deterministic, and cheap enough to run at 4x speed.

1. World representation for planning:
   - A compact `GoapWorldState` of typed boolean and small-integer keys, per NPC, built from
     what that NPC actually knows — never from ground truth. An NPC that has not seen the
     player must not plan as if it had.
   - Keys are declared in one place and validated against goap_action.csv at load time.

2. Planner:
   - A* over actions with preconditions and effects, driven by goap_action.csv costs,
     with a hard node budget per plan and a cached plan reused until invalidated.
   - Deterministic tie-breaking by action id. No hash-order iteration anywhere.
   - Replanning is triggered by events (perception change, suspicion threshold, noise heard,
     ally down, alarm band change), not polled every step. Budget: at most N plans per step
     site-wide, with a fair round-robin queue so no NPC is starved. Make N a table value.

3. Goals per archetype, from goap_goal.csv, with dynamic priority:
   Patrol, InvestigateNoise, InvestigateSighting, PursueTarget, CallForBackup,
   RaiseAlarm, TakeCover, Engage, FleeAndReport, GuardObjective, ReviveAlly,
   SecurePrisoner, RestorePower, CloseOpenDoor, ReportMissingColleague.
   Civilians get their own set: GoAboutBusiness, Panic, Flee, Hide, ReportToGuard.

4. Emergent behaviours that must fall out of the system, each with a dedicated test:
   - A guard finds an open door that should be closed, becomes suspicious and investigates.
   - A guard notices a colleague is missing from a patrol and reports it.
   - A guard finds a body, raises the alarm immediately, and the alarm band jumps.
   - A hidden body delays that discovery; the hiding place quality matters.
   - A radio-carrying guard that spots an intruder escalates the whole site; cutting the radio
     or taking them down silently before they transmit prevents it.
   - A panicked civilian runs to the nearest guard and raises suspicion without seeing anything.

5. Legibility requirement: every NPC exposes its current goal, current action, suspicion level
   and last known information, as structured data. The player must be able to understand why
   a guard did something; a stealth game whose AI is opaque is a frustrating one.
   The debug surface for this is mandatory, not optional.

6. Performance: 30 NPCs planning on a tier-4 site must cost under 2 ms per step on a mid-range
   CPU. Include a benchmark in the test project that fails if it regresses past that budget.

Tests: planner returns identical plans for identical inputs; no plan exceeds the node budget;
NPCs never act on information they do not possess (write this as an explicit test that
corrupts ground truth and asserts behaviour is unchanged); the six emergent behaviours above.
````

---

# 7. Stage 4e — Squad, roles, command post, mission lifecycle

````text
Stage 4e — Make it a squad game rather than a one-character game.

1. Squad composition at dispatch: 3-5 agents, each assigned a Role from agent_role.csv.
   Validate at dispatch: stamina thresholds, injuries, required role coverage for the
   objective type, gadget assignment, and total carry weight.

2. Control model:
   - Exactly one agent is Directly Controlled at a time; the player switches freely.
   - Non-controlled agents run their Role behaviour: a constrained, readable behaviour set
     (not GOAP — the player must be able to predict their own squad) built on the same
     tactical actions. Examples: PointMan advances to the next marked waypoint and holds;
     Overwatch stays put facing a direction and reports contacts; Mule picks up and carries
     loot and downed allies; Hacker moves to the nearest hackable and works it; Medic moves to
     the most injured ally.
   - Orders the player can issue to any non-controlled agent: MoveTo, HoldPosition, Follow,
     Stack on connection, Overwatch direction, UseGadget, Hide body, Carry ally, Regroup,
     Abort to extraction. Orders queue per agent and are visible in the UI.
   - A "hold all" and "resume all" command, plus the ability to issue orders while paused.
     Pausing and issuing orders is a core part of how this game is played; design for it.

3. Forward command post, when the site template has one:
   - A real location with its own small layout, holding the Handler role.
   - Grants support abilities on a cooldown measured in steps: ping last known guard positions,
     remote door unlock if hacked, camera feed on a chosen room, request extraction,
     emergency smoke drop.
   - Can be compromised: if the site alarm reaches a band and a responder reaches the post,
     the Handler is at risk and support abilities go offline. This gives the player a second
     front to worry about.

4. Mission lifecycle:
   Dispatch (strategic) -> Travel (strategic ticks) -> Insert -> TACTICAL -> Resolve ->
   Return (strategic ticks) -> Debrief.
   - Resolve classifies the outcome: CleanSuccess, Success, Compromised, Aborted, Disaster,
     with rewards, Heat, evidence and consequences per class.
   - Agents not at an extraction point when the mission ends are captured or killed per
     stage 4c rules.
   - Debrief produces a structured `MissionReport` with the full timeline, every perception
     and noise event, every roll with its modifier breakdown behind a debug flag, loot, exp,
     injuries, mental damage, and narrative summary lines.

5. Objective types, each with real tactical rules rather than a shared stub:
   StealData (reach terminal, hack for N steps, exfiltrate), Sabotage (plant, arm timer,
   be clear of the blast interval), Assassinate (reach target, who flees when alarmed),
   Rescue (find holding room, free the prisoner who then follows and is slow and loud),
   PlantBug (place and leave undetected — alarm above a band fails it even if you escape),
   Recon (observe N marked rooms without ever being Identified).

6. Mid-mission abort: always available, always reaching extraction takes real steps.
   Aborting with the objective incomplete must feel like a legitimate, sometimes correct choice.

Tests: role behaviours never issue an illegal action; orders survive save/load mid-mission;
each objective type has a headless scripted run that completes and one that fails correctly;
capture and rescue round-trip across two missions and a strategic interval.
````

---

# 8. Stage 5 — Save / load / replay (ฉบับปรับใหม่)

````text
Stage 5 — Bulletproof persistence, now including mid-mission saves. Save bugs kill reviews.

1. `SaveFile` = Header + WorldState, MessagePack with explicit [Key(n)] everywhere,
   LZ4-compressed, checksummed, written atomically (.tmp then move, with an fsync).
   Header: schema version, build version, world seed, playtime, agency name, mode
   (Strategic or Tactical), and a short description of where the player is.

2. Mid-mission saving is required, not optional:
   - The entire TacticalState serializes: entities, positions, postures, suspicion, GOAP plans
     and NPC knowledge, noise in flight, door and light states, fog state, order queues,
     objective progress, and all RNG stream states.
   - Loading a tactical save must resume at the exact step with identical subsequent behaviour.
     Prove it: save at step N, continue to N+5000 recording a state hash, reload the save,
     replay the same commands, assert the hash matches.
   - Ironman mode saves on a timer during tactical play too, so quitting mid-mission is safe
     but reloading to undo a mistake is not possible.

3. Migration framework with numbered `ISaveMigration` steps applied in order, a no-op v1->v2
   example plus its test, polite refusal of saves newer than the build, and a
   `SaveIntegrityReport` quarantining ids that no longer exist in the data tables rather than
   crashing or silently dropping them.

4. Replay: seed + ordered command log + state hash checkpoints every 1,000 strategic ticks and
   every 1,000 tactical steps. `ReplayVerifier` reports the first divergent checkpoint with the
   offending command and a diff of the two states. Make that output genuinely useful — it is
   the tool that will find every determinism bug from here on.

5. Determinism audit: reflect over the ProjectSpy.Core assembly and fail a test if it
   references UnityEngine, DateTime, System.Random, Guid, File, Directory or Environment.
   Separately grep for float and double in rule paths and justify or remove every hit.
   Paste both results.

6. Autosave policy: every in-game day in strategic mode, every 2 in-game minutes in tactical
   mode, 5 rotating slots plus manual slots plus one ironman slot.

Deliver docs/SAVE-FORMAT.md covering layout, versioning policy, the tactical state contract
and the migration procedure.
````

---

# 9. Stage 6 — Headless playability (จุดตัดสินชะตา)

````text
Stage 6 — Prove the game is fun in a terminal before spending one hour on 3D art.
If it is not tense here, no amount of lighting will save it.

1. `src/ProjectSpy.Sim/` console app, three modes:
   - `play` — interactive text client for BOTH scales.
     Strategic: base status, roster, rooms, contracts, sleeper operations, advance ticks.
     Tactical: render the current floor as an ASCII lane with positions, light intervals,
     known guard positions, fog state and connections; accept movement and action commands;
     support pause, step-by-step, and squad switching. I must be able to play a complete
     infiltration from this screen and feel tension.
   - `sim` — N autonomous agencies for M days in parallel with scripted policies, emitting CSV
     and a markdown report.
   - `verify` — run the replay verifier over a directory of replays.

2. Tactical AI policies for the squad so missions can run headless: Stealth (avoids all
   contact), Aggressive (eliminates guards), Speedrun (shortest path, ignores noise),
   Cautious (aborts above an alarm threshold). The control group matters: Speedrun should
   usually fail on high tiers, and if it does not, the design is broken.

3. Balance report, written to docs/BALANCE-<date>.md:
   - Funds and bankruptcy day per policy; agent churn per 100 days (dead, captured, burned out,
     resigned); mission outcome distribution by tier and by squad policy.
   - Sleeper economy: average ticks to each intel band, discovery rate, poison rate, and the
     measured success-rate difference between going in at 0%, 50% and 100% intel.
     That difference IS the value proposition of the whole sleeper system — if it is small,
     the system is pointless and must be retuned.
   - Stealth viability: fraction of missions completed without a single Identified perception.
   - Alarm trajectory histograms; time-to-extraction distributions.
   - Perception legibility: how often a mission was lost to something the player could not
     have perceived. This number must be low.
   - Flag as warnings: any tier above 90% or below 25% success; any room never worth building;
     any trait with no measurable effect; any dominant strategy beating all others by over 2x;
     any guard archetype that never meaningfully threatens.

4. Run at 100, 300 and 500 days across all policies and present a proposed CSV diff.
   DO NOT edit the data tables until I approve the diff.

5. `docs/DESIGN-REVIEW.md` — your blunt critique of the simulation as a game. Where is the
   tension, where is the boredom, which decisions are real and which are obviously-correct
   non-choices, and is the tactical layer actually more interesting than an abstract roll.
   I would rather hear it now than after the art is made.
````

Stage 6 — Remediation pass. Do this BEFORE the balance report, DESIGN-REVIEW, or any further
`play` work. Follow @knowledge.md. Stop and report at any gate marked STOP.

Ground rules for this pass
- Do not change any existing value in data/*.csv. If a fix needs a NEW tunable (a ceiling,
  a radius, a threshold), add it as a table row/column per rule 3, and list every such
  addition in the final report. Proposed changes to EXISTING values go in a CSV diff
  proposal only, and wait for my approval.
- Player input and policies must keep using the same PendingOrders channel and the same
  ActionSystem.Validate gate (rules 8, 14). No policy-only back doors.
- Every new roll uses its own named stream; no float in rule paths.

1. Regression tests for the nine defects
   For each defect below, write a test that FAILS against the pre-fix behaviour. Prove it:
   temporarily revert the fix (or construct the broken state) and show the test goes red,
   then restore and show it green. Paste the red/green evidence.
   a. ObserveRoom is called: the spawn room is observed at step 0; observing follows
      perception, not entry (walking past a closed door reveals nothing).
   b. Agents record sightings of guards (RecordSighting): a guard in an agent's cone appears
      in known-guard data with no suspicion side effect; a guard behind a closed door does not.
   c. Closed doors block sightlines via ConnectionState, not only the door type default.
   d. Objective work is room-scoped: a Hacker working a terminal outside the objective room
      does not advance progress.
   e. SeedObjective picks the work interactable in the OBJECTIVE room, not the first terminal
      on the site.
   f. The objective room always contains a work interactable (structural guarantee).
   g. `order move <room>` crosses rooms and floors via traversals; assert the agent ends up
      inside the target room, not at the wall beside the door.
   h. A squad containing a Handler can finish; TacticalState.IsStillInside is the single
      predicate used by both mission end and resolution; a clean exfil is never scored Disaster.
   i. SquadPolicyDriver.WalkTo uses the same traversal path as (g).
   Then add the structural nets that would have caught all of them:
   - Invariant 7 in SiteGeneratorTests: every site template x every allowed objective type
     has a reachable work interactable in the objective room. Run over the 20,000-site sweep.
   - For every site template x objective type, a scripted run must reach a non-InProgress
     outcome before the step ceiling. Hitting the ceiling FAILS the test. The ceiling lives
     in a table, not in code.
   - Every headless mission in the sweep asserts the same thing and counts ceiling hits
     as a hard failure in the report, not a data point.

2. Locked doors
   - First measure, do not guess: over the sweep, report (i) the fraction of sites where every
     route to the objective passes through a locked connection, (ii) policy_blocked_by_door
     per policy and tier.
   - Teach policies to handle locks using existing tactical actions only (pick, force, or
     route around). Intended character: Stealth prefers an unlocked alternate route, then
     pick, force only as a last resort; Speedrun forces; Aggressive forces; Cautious behaves
     like Stealth. Route cost must include lock penalties via table values.
   - Re-measure policy_blocked_by_door. If it is still high, do NOT edit site_gen_rule;
     put a proposed locked-door percentage in the CSV diff with the measured evidence.

3. Make stealth measurable (investigate before tuning)
   - Add per-mission diagnostics: guard->agent perception counts by level (None/Noticed/
     Identified), minimum guard-to-agent distance, steps spent with an agent inside any
     guard cone, steps spent Lit/Dim/Dark, peak suspicion, peak alarm band.
   - Answer with data why the squad is never seen: do patrols never overlap the policies'
     paths, are vision ranges/cones too small, is light always Dark, or do policies avoid
     contact by accident of pathing? Report the answer in plain words.
   - Do not tune values. If a code defect explains it, fix it and add a test. If it is a
     tuning issue, put it in the CSV diff.
   - Control checks that must hold: Speedrun is Identified more often than Stealth; Aggressive
     reaches a higher peak alarm band than Stealth; Cautious aborts in at least some runs.
   - GATE (STOP): if Stealth and Cautious are still byte-identical, or Speedrun is not seen
     more than Stealth, stop here and report the diagnostics. Do not continue to step 4/5.

4. Statistical honesty
   - Seed count per cell becomes a CLI parameter, default 40. Results must be identical
     regardless of parallelism (deterministic ordering of runs and aggregation).
   - Report Wilson 95% confidence intervals per cell. Only call a difference between
     policies real if the intervals do not overlap; otherwise label it "not distinguishable".
   - Investigate the anomaly that tier 3-4 succeeds more than tier 1-2: break failures down
     by template and by failure reason per cell, and say which reasons dominate.
   - Rerun the sweep and show the new table next to the old 4-seed table.

5. Only if the gate in step 3 passed: write docs/BALANCE-<date>.md per the original Stage 6
   brief item 3, the proposed CSV diff (item 4, unapplied), and docs/DESIGN-REVIEW.md
   (item 5, blunt). Run at 100, 300 and 500 days as specified.

Deferred, but not silent
- `play strategic` and `verify` stay out of scope. Confirm each throws NotImplementedException
  with a `// TODO(stage-N):` comment naming the stage (rule 5). No empty bodies.

Final report format
- Table: step -> done / not done / blocked, with test counts before and after.
- Evidence for red-then-green on each of the nine regression tests.
- Every new table row/column added.
- Every place you found the code contradicting knowledge.md.
- The answer to "why was the squad never seen", in plain words.
- A list of things you could not verify.

---

# 10. Stage 7 — Unity bootstrap (URP 3D)

````text
Stage 7 — Bring up the Unity project around the finished Core. 2.5D, URP 3D.

1. Create UnityProject on Unity 6.3 LTS with URP (3D). Packages: VContainer, UniTask,
   R3, Cinemachine, Newtonsoft Json; reference Steamworks.NET without integrating it yet.
   Configure URP: forward+ renderer, shadows on for a single key light per room, SRP batcher on.

2. tools/sync-dlls.ps1 — publish ProjectSpy.Core and ProjectSpy.Tables as netstandard2.1,
   copy the DLLs and their non-conflicting dependencies into Assets/Plugins/ProjectSpy/,
   skipping anything Unity ships, printing a clear conflict list rather than failing silently.

3. Asmdefs: ProjectSpy.Unity (runtime) and ProjectSpy.Unity.Editor. Add an editor-time
   validator that flags any Presentation type performing arithmetic on Core stat or position
   fields — game rules must not drift into Unity.

4. Editor automation under a ProjectSpy/Setup menu:
   - `BlockoutArtGenerator` — primitive placeholder 3D content: a modular room kit
     (floor, back wall, side wall, ceiling, door frame, stair, ladder, vent) as grey boxes
     with correct real-world scale where 1 lane unit = 1 metre; a capsule agent prefab with a
     facing indicator; coloured markers for guards, civilians, loot, terminals and lights.
     No textures, no detail — scale and silhouette only.
   - `SiteAssembler` — the critical piece: take a Core `SiteLayout` and instantiate a
     continuous building from the modular kit, floors stacked with correct storey height,
     rooms laid along the X axis at their lane intervals, connections placed at their real
     positions. It must handle any generated layout without manual fixing. Expose it both at
     runtime and as an editor preview tool so I can eyeball 50 generated buildings quickly.
   - `PrefabGenerator`, `SceneGenerator` (Boot index 0, Base, Tactical; Base and Tactical load
     additively over a persistent root), `ProjectValidator`, `BatchSetup.RunFullSetup()`.

5. Runtime infrastructure in RootLifetimeScope: GameSession wrapper, TableService, SaveService,
   SceneRouter, StrategicTimeController, TacticalTimeController, WindowService, ToastService,
   AudioService, LocalizationService, InputService (new Input System, rebindable).

6. `SimulationRunner` — converts real seconds into Core steps or ticks per the active mode and
   speed, calls the right Advance method, and republishes Core events onto an R3 Subject.
   Rendering interpolates entity transforms between steps; the simulation never interpolates.
   Pausing stops calling Advance; it must never change what Advance does.

7. Update UnityProject/SETUP.md so the remaining manual steps are at most three items.
````

---

# 11. Stage 8 — UI + Base scene

````text
Stage 8 — The strategic layer. This game is its UI; budget accordingly.
Layout reference: Idol Manager. Mood reference: Lobotomy Corporation.

1. UI foundation: UIRoot with layers World/HUD/Window/Modal/Toast/Tooltip; a stack-based
   pooled WindowService with UniTask-returning OpenAsync, Escape closing the top, modal
   raycast blocking, and optional auto-pause on open; a ToastService; and a TooltipService
   that shows the FULL modifier breakdown for every number ("Infiltration 62 = base 50
   +8 training +10 Ghost -6 fatigue"). Hiding the maths in a management game is a design bug.
   All binding through R3 from the Core event stream — never poll Core in Update().

2. Top bar: date and weekday, Pause/1x/4x/16x/64x with keyboard bindings, Funds with the
   weekly net delta in green or red, Intel, Materials, Reputation, and a Heat gauge whose
   presentation visibly darkens as it climbs.

3. Base cutaway view in 3D: rooms rendered from the modular kit at their grid positions,
   agents as small figures performing their assigned activity, construction and damage states,
   a subtle camera parallax on pan. Build mode with a catalogue filtered by act, ghost preview
   with validity colouring, cost and upkeep and depth modifier shown before confirming.
   Click a room for its detail panel; drag an agent from the roster to assign, with invalid
   targets explaining why.

4. Roster panel: sortable and filterable list with portrait, codename, class, status, physical
   and mental stamina bars and the five skills. The agent detail window is where the player
   forms attachments — full stats, visible traits, career timeline, mission record, salary,
   a plain-language loyalty mood line, and relationships with other agents. Make it good.

5. Operations screens:
   - Contracts: client, objective type, tier, estimated difficulty against your roster,
     reward, heat gain, expiry. The difficulty estimate must be an honest number derived from
     Core, never a fudge.
   - Sleeper Operations: per-site intel percent with a progress bar, elapsed and estimated
     remaining ticks, current discovery risk per tick, what the next band will unlock, and
     recall and reassign actions. Discovered operations show their status; poisoned ones do not.
   - Mission preparation: select 3-5 agents, assign roles and gadgets, see a readiness
     readout, review the intel snapshot as a floor-by-floor preview with confidence markers
     on every fact, then dispatch.

6. Localization wired throughout with Thai as default and a language toggle; missing keys
   render as the key with a console warning, never an exception.

EditMode tests for pure presenter logic: affordability gating, placement validity, sort and
filter correctness, readiness maths, intel band preview correctness.
````

---

# 12. Stage 9a — Tactical presentation

````text
Stage 9a — The tactical scene: control, camera, lighting. This is the screenshot that sells it.

1. Site rendering: assemble the building with SiteAssembler, render only the floors the camera
   covers, and fade out intervening geometry so the cutaway always reads clearly.
   Rooms the squad has never observed render as near-black voids, not as grey boxes.

2. Lighting as gameplay:
   - Each Core light interval drives a real URP light; destroying or switching one changes both
     the render and the Core light state in the same step.
   - Agents in Dark render as silhouettes; Dim is partial; Lit is fully visible.
   - A readable on-screen indicator of the controlled agent's current light state, because the
     player must never be surprised by being seen while standing in what looked like shadow.
   - Baked ambient plus a small number of realtime lights; budget and document the limit.

3. Direct control: WASD or stick movement along the lane, posture cycling, context interaction
   on a single key with a clear prompt, aim and throw with a rendered arc preview matching the
   Core range check exactly (if the arc shown is not the arc computed, that is a bug).
   Switching the controlled agent with number keys and a squad bar.

4. Squad orders UI: a radial or hotbar order menu usable while paused, order markers rendered
   in the world, per-agent order queues visible on the squad bar, and a clear distinction
   between an agent following orders and an agent idle.

5. Perception feedback — the most important UI in the game:
   - Guard vision cones rendered as light-footprint arcs only where the player has reason to
     know them (observed directly, or reported by intel with a distinct stale styling).
   - A per-guard suspicion indicator with clear states and a fill that rises visibly.
   - A detection meter on the controlled agent showing who is perceiving them and how fast.
   - Noise visualised as expanding rings at their real Core radius, with a different style for
     noise the squad made and noise heard from elsewhere.
   - Last-known-position markers where a guard believes the player to be.

6. Camera: Cinemachine rig locked to the cutaway angle, following the controlled agent with a
   dead zone, framing wider as alarm rises, manual pan and zoom clamped to site bounds, and a
   tactical overview mode showing the whole building with fog applied.

7. Fog presentation: Unknown rooms black; Reported rooms drawn from intel in a distinct
   desaturated, slightly wrong-looking style; Observed rooms fully rendered. When an observed
   fact contradicts a reported one, play a short, unmistakable contradiction beat — this is a
   gameplay moment the sleeper system exists to create.
````

---

# 13. Stage 9b — Tension, audio, after-action

````text
Stage 9b — Make infiltration feel like infiltration.

1. Alarm band presentation: each of the five bands has its own music layer, light colour shift,
   vignette, and a one-line in-world message ("Security sweep initiated — east stairwell
   sealed"). Band transitions are a full-screen moment but never block input.

2. Audio with pooled sources and occlusion driven by Core's connection graph rather than by
   Unity physics: footsteps per posture and surface, doors, vents, hacking, radio chatter,
   alarm stingers per band, melee, takedown, gunfire, body drag, breathing that quickens as
   detection rises, and an ambient bed that layers in tension with the alarm.
   Generate silent placeholder clips via an editor script so nothing is ever null.

3. Animation: a small, complete set on the blockout capsule first — idle, walk, crouch-walk,
   prone, climb, vault, interact, throw, melee, takedown, hurt, downed, carry, drag, die.
   Prove the control loop feels right on capsules before any character art is commissioned.

4. Moment-to-moment juice: takedown camera emphasis, a brief slowdown when first Identified,
   screen edge pulses when a guard is about to see you, a satisfying body-hide confirmation,
   and a distinct audiovisual language for lethal versus non-lethal resolution. All of it
   toggleable in accessibility settings.

5. After-action report: route taken per agent rendered on a floor diagram, timeline of every
   perception, noise and alarm event, loot, exp, injuries, mental damage, intel contradictions
   encountered, and the narrative summary. Exportable to clipboard as text — players share
   these and so will bug reporters.

6. Capture and loss presentation: when an agent is captured, the game must show that it does
   not know where they went unless intel is at 100%. Present it as an unresolved thread on the
   strategic screen with a countdown, not as a failure screen. When an agent dies, give it
   weight — a quiet beat, their file closing, their record preserved for the epilogue.

7. Performance pass on the tactical scene: 60 fps on a mid-range laptop with 30 NPCs on a
   tier-4 site, zero per-frame managed allocation in the control and render loops, and
   sub-2-second load into a mission. Include a repeatable profiling scene and record the
   baseline numbers in docs/PERFORMANCE.md.
````

---

# 14. Stage 10 — Meta, Steam, release

````text
Stage 10 — Everything between "it works" and "it ships".

1. Campaign: `StoryDirector` in Core driving acts from flags, elapsed days and milestones,
   unlocking rooms, site tiers, gadgets and antagonists. A `story_event.csv` of at least 40
   dilemma events with conditions, weights, cooldowns and 2-4 real-consequence choices,
   written in Thai first with English parallel. Three endings gated on Reputation, Heat, Intel
   and surviving roster, plus an epilogue naming every agent who ever served and what became
   of them. That epilogue is the emotional payoff of permadeath — do not cut it.
   Endless mode after any ending with escalating site tiers and a local score table.

2. Difficulty and accessibility: presets altering economy pressure, guard competence, capture
   and death chance, sleeper discovery rate and alarm decay, plus a custom mode exposing those
   sliders. Ironman toggle. UI scale, colourblind-safe alarm and perception palettes, full
   keyboard navigation, screen shake and flash toggles, adjustable text speed, dyslexia font,
   and an option to show raw rolls instead of narrative framing.

3. Steam via Steamworks.NET behind an `IPlatformService` so the game still runs with no Steam
   client: 30 achievements spanning early, mid, late and interesting-failure categories;
   Cloud save sync with a conflict dialog; rich presence showing act, day and agency name;
   docs/STEAM-CHECKLIST.md covering store assets, capsule sizes, branches, depots and a demo
   build plan limited to act 1.

4. Stability: a global exception handler writing a crash report with the last 200 log lines,
   the current save and the replay log, plus a dialog explaining how to send it to me.
   Fuzz Core with random and malformed commands and assert the world is either unchanged or
   legally changed, never corrupted. Load every save produced by every earlier stage and
   confirm migrations work. Re-run the balance simulator and update docs/BALANCE.md.

5. `/review` the whole repository against @knowledge.md. Report findings in a severity table
   covering: any UnityEngine reference in Core, any game rule implemented in Presentation,
   any float in a rule path, any non-deterministic iteration, any MessagePack member without
   an explicit Key, any hard-coded player-facing string, any magic number that should be a
   table row, and any tactical action that ignores its heat or evidence cost.
   Fix everything high and medium.

6. Release package: docs/RELEASE-CHECKLIST.md, docs/KNOWN-ISSUES.md, a bug report template,
   Thai and English player guides, and a playtest feedback form. Tag v0.9.0-beta and write an
   honest summary of what is still blockout, what is stubbed, and what a playtester might
   mistake for a finished feature.
````

---


ลำดับที่ควรยิง

Stage 1 และ 2 เสร็จแล้ว ผมเห็น MOLE_DESIGN.md ในโฟลเดอร์ docs จึงเดาว่า Stage 3 เสร็จด้วย ชุดใหม่ไม่ได้รวม Stage 3 ไว้ ถ้ายังไม่ได้ทำให้ยิง Stage 3 ก่อน

Prompt 0 (แก้ knowledge.md แต่ใช้ฉบับที่ผมแก้ให้ด้านล่าง ไม่ใช่ฉบับในชุด)
Stage 3b จาก prompt.md ที่เพิ่งแก้ (ปรับ BaseLayout เป็น Layer/SlotIndex ถ้ายังไม่ได้ทำ)
Patch Stage 1 → Patch Stage 2
Stage 4a → 4b → 4c → 4d → 4e
Stage 5 → 6 (ในชุดใหม่นับเป็นข้อ 8 และ 9)

4a/4b/5/6 ที่ผมเพิ่งแก้ให้ใน prompt.md ถูกแทนที่ด้วยชุดใหม่ทั้งหมด เพราะชุดใหม่เปลี่ยนภารกิจจากกราฟ node นามธรรมเป็นอาคารต่อเนื่องที่เดินได้จริง ถ้ายังไม่ได้ยิง 4a เก่าก็ไม่มีอะไรต้องรื้อ