# The mole — design and reasoning chain

Stage 3 gives the agency an insider. This document explains why the mole is a fair
mystery rather than a hidden dice roll, and gives a designer enough to tune it.

The requirement was: *"a player paying attention to Heat spikes versus mission logs
should be able to narrow it down."* Everything below exists to make that sentence
literally true.

---

## 1. The problem with hidden traitors

A mole that is unfair in either direction is not interesting:

- **Unfairly hidden** (indistinguishable from noise) means the counter-intel room is a
  waste of money and the correct play is to never investigate anyone. The player has
  no decision to make.
- **Unfairly obvious** (evidence points straight at whoever it is) means the mystery
  is a formality. The player opens the desk, reads the number, and the game is over.

So the design has to keep both failure modes out. Three properties do that, and each
one is load-bearing — removing any of them collapses the system back into noise.

---

## 2. Property 1 — the contribution is deterministic

A mole adds exactly `counter_intel_rule.mole_weekly_heat` Heat every week. Not on a
roll. Not scaled by anything.

This is the property that makes correlation possible at all. If the weekly
contribution varied, a player could not distinguish the mole's share of Heat from
ordinary drift, and no amount of careful note-taking would narrow anything down. The
whole reasoning chain starts from "the spike is the same size every week", and that has
to be true in the data, not merely in the designer's intent.

`MoleSystemTests.NoMolesMeansNoHiddenHeat` pins the other half: with no mole the weekly
pass is a strict no-op, so any spike the player observes is attributable.

### The patriot floor

A `HeatGainReduction` trait can talk the spike down, but only so far.
`counter_intel_rule.mole_heat_floor_percent` guarantees the spike never drops below a
quarter of its normal size.

This clamp exists because the first version did not have it. A patriot's reduction
(15) exceeded the weekly heat (6), so a single trait silenced the mole completely —
the tell vanished and the mole cost the player nothing. Counter-play should make the
mystery harder to read, not make it unreadable.
`MoleSystemTests.NoTraitCombinationCanSilenceTheMoleEntirely` guards it.

---

## 3. Property 2 — the timing is correlatable

Two independent public signals point at the same week:

| Signal | Source | What the player sees |
|---|---|---|
| Heat spike | `ApplyWeeklyMoleHeat`, on weekly settlement | Heat ticks up on a quiet week |
| Mission log | `RollMissionLeak`, recorded per mission | A job went harder than it should have |

Both land on ticks the player can name. Heat lands on the settlement day, which is a
fixed weekday (`economy_rule.settlement_day_of_week`). Leaks are stamped with the tick
they occurred on. Neither signal requires opening a debug view.

This is why the leak is recorded in `CounterIntelState.LeakLog` *before* the mole is
exposed. The player accumulates a list of suspicious weeks while still having no idea
who is responsible — and that list is the evidence they will later be able to check
the answer against.

A leak is **not public** until exposure (`MoleLeak.IsPublic`). Before that the player
sees "something went wrong on this job", not "someone in my house did it". Exposing a
mole retroactively flips their past leaks to public, which is what closes the loop on
the Heat the player was already correlating.

### Heat is deliberately not attributable on its own

Missions and raids also raise Heat. That is intentional friction. It forces the
cross-referencing that the brief asks for: the player has to eliminate the ordinary
sources before the residual spike means anything. Removing this would make
counter-intel pointless — the player would never need the mission log.

---

## 4. Property 3 — evidence is graded, not a verdict

An investigation accumulates evidence on a 0–100 scale, one step at a time:

- `investigation_evidence_chance` (35%) — gain `investigation_evidence_gain` (10)
- `investigation_false_lead_chance` (20%) — lose `investigation_false_lead_penalty` (8)
- otherwise — nothing happens

A false lead lowers evidence rather than merely failing to raise it. A weak case
genuinely deteriorates, which is what makes the player cautious about committing.

### The two thresholds, and why they are related

| Rule | Value | Meaning |
|---|---|---|
| `investigation_max_steps` | 10 | Steps before a case is called |
| `expose_threshold` | 60 | Evidence needed to expose outright |
| best case over 10 steps | 100 | 10 × 10 |
| expected case over 10 steps | 19 | 10 × (0.35·10 − 0.20·8) |

These three numbers only make sense together, which is why they live in one table and
why `TableValidator.ValidateCounterIntelBalance` asserts the relationship:

1. **Best case must clear the threshold.** Otherwise a case can never conclude and the
   mole is uncatchable by investigation. This was a real bug: evidence moved by one
   per step against a hardcoded threshold of 80, so reaching it was arithmetically
   impossible. The desk silently did nothing, forever, with no error anywhere.
2. **The expected case must *not* clear it.** Evidence accrues identically whoever you
   investigate, so if the average case cleared the threshold the desk would expose
   people at random and the player's own correlation work would be worth nothing. The
   desk converts a suspicion you already hold into proof; it does not supply the
   suspicion.

So a player who has correctly narrowed the field and investigates the right person
still needs a reasonably good run of rolls. A player who guesses has almost none. That
asymmetry is the whole game.

### Two ways a case can end

Both go through `MoleSystem.Conclude`, deliberately. When these were separate paths, a
case that crossed the threshold mid-investigation was marked `Exposed` while the
agent's hidden traits stayed hidden — the desk announced a verdict and then did
nothing with it. A conclusive result now exposes the agent on either path, and
`MoleSystemTests.AConclusiveCaseExposesTheMole` pins it.

Clearing the threshold against an innocent agent **clears** them rather than exposing
them. Reaching a high score is not the same as being the mole; the desk grades a case,
it does not pronounce judgement on someone's character.

---

## 5. The cost of being wrong

A wrong accusation costs `wrong_accusation_loyalty_drain` (12) loyalty to **every
active agent**, not just the accused one.

This is the sharpest number in the system and it is deliberately organisation-wide. The
damage being modelled is not the innocent person's feelings — it is the atmosphere of a
place where accusing people gets people fired. A player who can accuse one suspect at
no cost has no decision to make; a player who can bankrupt the whole roster by being
wrong has to earn the accusation first.

Note also that evidence does not gate the accusation. You may always accuse. Evidence
only converts into an *automatic* exposure when the case concludes. The cost, not the
lock, is what disciplines the player.

---

## 6. What the player actually does, end to end

1. Notice Heat higher than the missions account for.
2. Note which week it spiked; check the mission log for a leak in the same week.
3. Repeat over several weeks. The mole's contribution is constant, so the residual
   spike is stable and attributable.
4. Narrow the roster to whoever was present and unaccounted for in those weeks.
5. Open an investigation on your best candidate. Watch evidence move; expect false
   leads.
6. Either the case concludes and exposes them, or you accuse on your own reasoning and
   accept that the whole agency pays if you are wrong.

Step 3 is the load-bearing one and it is the reason step 2's determinism requirement
matters: without a constant contribution, repeated observation would not narrow
anything.

---

## 7. Tuning notes

If you change any of these, run the tests — `MoleSystemTests` and
`TableValidatorTests` will tell you what you broke.

| Symptom | Likely cause |
|---|---|
| Nobody is ever exposed by investigation | `expose_threshold` too high for `investigation_max_steps × investigation_evidence_gain` |
| Everybody gets exposed eventually | `expose_threshold` below the expected case; the desk has stopped needing your reasoning |
| The mole costs the player nothing | `mole_heat_floor_percent` too high, or no `HeatGainReduction` trait exists |
| The mole is unfindable | `mole_weekly_heat` too small to separate from ordinary Heat drift |
| Accusations feel meaningless | `wrong_accusation_loyalty_drain` too low relative to average loyalty |

## 8. Honest limitations

- **Heat is a shared counter.** With several moles, or with heavy mission activity, the
  residual spike is harder to attribute. This is a deliberate difficulty knob, not an
  oversight, but it does mean the reasoning chain is easiest on a quiet base.
- **Evidence is not discriminative.** Investigating the mole and investigating an
  innocent follow the same distribution. The desk cannot tell you it has the right
  person; only the player's own correlation can. If a future stage wants the desk to
  be a genuine detector rather than a confidence generator, that is a deliberate
  design change and it would make the Heat/mission-log chain redundant.
- **The mole's Heat does not stop on exposure.** An exposed mole keeps accruing Heat,
  because otherwise uncovering one would be a reward for playing well rather than a
  consequence of having had one. Firing them does remove the contribution, which is the
  player's actual remedy.
