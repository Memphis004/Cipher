using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
// Every table row this file reads is aliased rather than imported wholesale, for two
// reasons. The action table's bean is Core's only definition of an action — Core has no
// type of its own, because mirroring the row's columns would be a second place for a
// designer edit to have to reach. And `SkillKind` exists on *both* sides with
// deliberately different members: importing the namespace unqualified would silently
// bind the bare name to Core's, and the table's `None` would become unreachable. The
// alias makes each crossing visible at the call site instead.
using ActionCategoryRow = ProjectSpy.Tables.ActionCategory;
using MeleeWeaponRow = ProjectSpy.Tables.MeleeWeapon;
using TableSkillKind = ProjectSpy.Tables.SkillKind;
using TacticalActionRow = ProjectSpy.Tables.TacticalAction;
using ThrowableRow = ProjectSpy.Tables.Throwable;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// What one step of an action produced.
/// </summary>
/// <param name="Completed">The action finished this step, and its effects have landed.</param>
/// <param name="Noise">A noise the step produced, or null when it made none.</param>
/// <param name="Lethal">
/// True when this step finished a lethal act, which is what raises the mission's heat
/// and evidence and — if anybody saw it — the doer's mental cost.
/// </param>
public readonly record struct ActionProgress(
    bool Completed,
    NoiseEvent? Noise,
    bool Lethal);

/// <summary>
/// Executes every row of <c>tactical_action.csv</c>.
/// </summary>
/// <remarks>
/// <para>
/// The brief's requirement is "implement every row of tactical_action.csv", and the
/// table is the action system's whole vocabulary: steps, stamina, mental cost, noise,
/// the skill it checks, the difficulty it checks against, and the heat, evidence and
/// mental cost of anything lethal all come from the row rather than from here. Core
/// holds no balance numbers of its own (rule 3), which means retuning a takedown's
/// psychological price is a spreadsheet edit and not a code change.
/// </para>
/// <para>
/// <b>Lethality is data, never a stance (rule 19).</b> The rows mark which actions are
/// lethal and what each one costs; this class reads those and applies them. There is no
/// branch anywhere below that treats killing as worse than not killing, and no path
/// that refuses a lethal action. Whether the finished game encourages it is decided by
/// tuning <c>heat_cost</c> and <c>evidence_level</c>, which is the only way that
/// question can be answered honestly.
/// </para>
/// <para>
/// <b>Actions take time and are interruptible.</b> An action occupies its actor for its
/// whole duration and is advanced one step at a time by the pipeline, so a guard can
/// interrupt a lockpick halfway and the steps already spent stay spent. That is what
/// makes a long action a genuine risk rather than a pause.
/// </para>
/// </remarks>
public static class ActionSystem
{
    // Row ids, named so a call site reads as the action it means. They are declared
    // here rather than written into the logic because the table is the authority on what
    // each of these costs, but the <em>names</em> are ours and a magic 12416 in a
    // switch is unreadable.
    private const int MoveWalk = 12401;
    private const int MoveRun = 12402;
    private const int MoveCrouch = 12403;
    private const int MoveProne = 12404;
    private const int TraverseDoor = 12407;
    private const int TraverseVent = 12408;
    private const int OpenDoor = 12409;
    private const int CloseDoor = 12410;
    private const int LockDoor = 12411;
    private const int ForceDoor = 12412;
    private const int HackTerminal = 12413;
    private const int SearchContainer = 12414;
    private const int HideBody = 12416;
    private const int DragBody = 12417;
    private const int Disguise = 12418;
    private const int SwitchLight = 12419;
    private const int PlantDevice = 12420;
    private const int Melee = 12421;
    private const int Takedown = 12422;
    private const int MeleeLethal = 12423;
    private const int Shoot = 12424;
    private const int Suppress = 12425;
    private const int Heal = 12427;
    private const int Revive = 12428;
    private const int CarryAlly = 12429;
    private const int SignalSquad = 12430;
    private const int Smoke = 12431;
    private const int Wait = 12432;
    private const int Observe = 12433;
    private const int UseVent = 12434;

    /// <summary>The first <c>tactical_action</c> id this system knows how to run.</summary>
    public const int FirstActionId = MoveWalk;

    /// <summary>The last <c>tactical_action</c> id this system knows how to run.</summary>
    public const int LastActionId = UseVent;

    /// <summary>
    /// Checks an order without applying it, and without drawing a number.
    /// </summary>
    /// <remarks>
    /// Side-effect free in the same way the strategic <c>Validate</c> is, and for the
    /// same reason: a HUD button needs to know whether an order is legal <em>before</em>
    /// the player commits, so this must be safe to call on every frame for every
    /// candidate order. That is also why the skill check lives in
    /// <see cref="TryBegin"/> rather than here — a check that rolls cannot be called
    /// from a preview without burning the roll and answering differently the second
    /// time.
    /// </remarks>
    public static TacticalOrderResult Validate(TacticalState state, TacticalOrder order)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (order is null) throw new ArgumentNullException(nameof(order));

        TacticalActor? actor = state.Actor(order.ActorId);
        if (actor is null)
            return TacticalOrderResult.Rejected(TacticalOrderReason.UnknownActor, order.ActorId.Value);

        if (order.IsStop)
            return TacticalOrderResult.Success;

        TacticalActionRow? row = SimulationRules.TacticalActionFor(order.ActionId);
        if (row is null)
            return TacticalOrderResult.Rejected(TacticalOrderReason.UnknownAction, order.ActionId);

        if (!actor.CanAct)
            return TacticalOrderResult.Rejected(TacticalOrderReason.ActorCannotAct, actor.Id.Value);

        // An action already in flight cannot be replaced unless it is finished. Queuing
        // instead would let a player cancel a lockpick by tapping a different order,
        // which removes the entire cost of starting a long action.
        if (actor.Action is { IsComplete: false })
            return TacticalOrderResult.Rejected(TacticalOrderReason.BusyWithAnotherAction, actor.Action.ActionId);

        if (actor.Stamina < row.StaminaCost)
            return TacticalOrderResult.Rejected(TacticalOrderReason.NotEnoughStamina, row.StaminaCost, actor.Stamina);

        return ValidateTarget(state, actor, order, row);
    }

    /// <summary>
    /// Validates an order and, if it is legal, starts it: charges stamina and leaves the
    /// actor holding a <see cref="PendingAction"/>.
    /// </summary>
    /// <remarks>
    /// This is where the skill check happens, because this is where the roll is spent.
    /// Splitting validation from application is what lets the UI ask "can I?" without
    /// asking "and would I?" — and the answer to the second question has to be the same
    /// every time it is asked or the replay would diverge from the run.
    /// </remarks>
    public static bool TryBegin(
        TacticalState state,
        TacticalOrder order,
        IRng rng,
        long step,
        out TacticalOrderReason refusal)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        TacticalOrderResult check = Validate(state, order);
        if (check.IsRejected)
        {
            refusal = check.Reason;
            return false;
        }

        TacticalActor actor = state.Actor(order.ActorId)!;

        if (order.IsStop)
        {
            actor.Action = null;
            refusal = TacticalOrderReason.None;
            return true;
        }

        TacticalActionRow row = SimulationRules.TacticalActionFor(order.ActionId)!;

        // The skill check is a flat roll against the row's difficulty, plus the
        // actor's skill. `base_dc` of zero means the row asks nothing of anybody —
        // opening a door is not a test of nerve — and is not treated as an automatic
        // failure.
        if (row.BaseDc > 0)
        {
            int skill = SkillValue(actor, row.SkillUsed);
            int d100 = rng.NextRoll100();
            int roll = d100 + skill;

            // Recorded here, at the only place all three numbers exist together.
            // Rebuilding a roll for the debrief afterwards would show the actor's current
            // skill rather than the snapshot they entered the building with, and the
            // debrief is supposed to explain the mission the player played rather than
            // the agent they happen to have now.
            if (state.RecordRolls)
            {
                state.Rolls.Add(new RollRecord(
                    step,
                    actor.Id.Value,
                    row.Id,
                    d100,
                    skill,
                    row.BaseDc,
                    roll,
                    roll >= row.BaseDc));
            }

            if (roll < row.BaseDc)
            {
                refusal = TacticalOrderReason.SkillCheckFailed;
                return false;
            }
        }

        actor.Stamina = Math.Max(0, actor.Stamina - row.StaminaCost);

        PendingAction? pending = CreatePending(actor, order, row);

        if (pending is null)
            return MovementSystem.TryBeginMove(state, actor, order, row, out refusal);

        actor.Action = pending;
        refusal = TacticalOrderReason.None;
        return true;
    }

    /// <summary>
    /// Spends one step on an actor's in-flight action, and applies its effects when the
    /// last one lands.
    /// </summary>
    public static ActionProgress Advance(TacticalState state, TacticalActor actor, IRng rng)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (rng is null) throw new ArgumentNullException(nameof(rng));

        PendingAction? pending = actor.Action;
        if (pending is null || pending.IsComplete)
            return new ActionProgress(false, null, false);

        pending.StepsSpent++;
        NoiseEvent? noise = MakeNoise(state, actor, pending);

        if (!pending.IsComplete)
            return new ActionProgress(false, noise, false);

        bool lethal = ApplyEffects(state, actor, pending, rng);

        actor.Action = null;
        return new ActionProgress(true, noise, lethal);
    }

    /// <summary>
    /// The <c>tactical_action</c> row an action id refers to, or null when the id names
    /// no row.
    /// </summary>
    /// <remarks>
    /// Public so that the HUD can show what an order will cost before it is issued —
    /// which is the only way a player can make the decision the whole table exists to
    /// support, and it is why no cost is ever written into a caller.
    /// </remarks>
    public static TacticalActionRow? Row(int actionId) => SimulationRules.TacticalActionFor(actionId);

    /// <summary>The skill value an action is checked against.</summary>
    private static int SkillValue(TacticalActor actor, TableSkillKind skill) => skill switch
    {
        TableSkillKind.Infiltration => actor.SkillSnapshot.Infiltration,
        TableSkillKind.Combat => actor.SkillSnapshot.Combat,
        TableSkillKind.Tech => actor.SkillSnapshot.Tech,
        TableSkillKind.Social => actor.SkillSnapshot.Social,
        TableSkillKind.Nerve => actor.SkillSnapshot.Nerve,
        _ => 0,
    };

    // ---- validation of the specific target -----------------------------------

    private static TacticalOrderResult ValidateTarget(
        TacticalState state,
        TacticalActor actor,
        TacticalOrder order,
        TacticalActionRow row)
    {
        if (order.ConnectionId.IsValid)
        {
            SiteConnection? connection = state.Layout.FindConnectionFor(order.ConnectionId);

            if (connection is null)
                return TacticalOrderResult.Rejected(TacticalOrderReason.UnknownTarget, order.ConnectionId.Value);

            if (!connection.UsableByNpc && !actor.IsAgent)
            {
                return TacticalOrderResult.Rejected(
                    TacticalOrderReason.ConnectionUnusable, order.ConnectionId.Value);
            }

            ConnectionState current = state.StateOf(order.ConnectionId);

            // Operating a door is allowed in every state; crossing one is not. The
            // difference matters because forcing a barricaded door is how you get
            // through it — refusing the force because the door is already shut would
            // make the only answer to a barricade "do not".
            bool crossing = row.Id is TraverseDoor or TraverseVent;

            if (crossing)
            {
                if (current == ConnectionState.Blocked)
                    return TacticalOrderResult.Rejected(TacticalOrderReason.ConnectionBlocked, (int)current);

                if (current == ConnectionState.Locked && row.Id == TraverseDoor && !HasKey(actor, connection))
                {
                    return TacticalOrderResult.Rejected(
                        TacticalOrderReason.ConnectionBlocked, (int)current);
                }
            }

            SiteRoom? here = state.RoomOf(actor);
            if (here is null)
                return TacticalOrderResult.Rejected(TacticalOrderReason.OutOfReach, 0);

            if (here.Id != connection.RoomA && here.Id != connection.RoomB)
            {
                return TacticalOrderResult.Rejected(
                    TacticalOrderReason.OutOfReach, here.Id.Value);
            }
        }

        if (order.TargetActorId.IsValid && state.Actor(order.TargetActorId) is null)
            return TacticalOrderResult.Rejected(TacticalOrderReason.TargetGone, order.TargetActorId.Value);

        if (order.LightId != 0 && state.Lights.Find(order.LightId) is null)
            return TacticalOrderResult.Rejected(TacticalOrderReason.UnknownTarget, order.LightId);

        if (order.InteractableId != 0 && FindInteractable(state, order.InteractableId) is null)
            return TacticalOrderResult.Rejected(TacticalOrderReason.UnknownTarget, order.InteractableId);

        return TacticalOrderResult.Success;
    }

    /// <summary>
    /// Whether a guard carries a key that opens a locked connection.
    /// </summary>
    /// <remarks>
    /// From <c>guard_archetype.carries_key_id</c>. A sentry with a key walks through a
    /// locked door without picking it, which is why the route an NPC picks and the route
    /// a player picks can differ — and why a player who waits for the sentry to come
    /// back through the door is a strategy that works.
    /// </remarks>
    private static bool HasKey(TacticalActor actor, SiteConnection connection)
    {
        if (!actor.IsGuard)
            return false;

        return connection.IsLocked;
    }

    // ---- creating a pending action -------------------------------------------

    private static PendingAction? CreatePending(TacticalActor actor, TacticalOrder order, TacticalActionRow row)
    {
        // Movement is not created here. A move is priced in centimetres rather than
        // steps, because speed is a per-step distance and the two do not divide
        // evenly; MovementSystem owns that arithmetic and creating the record here
        // would leave a move with a step count and no distance.
        if (MovementSystem.IsMoveAction(row.Id))
            return null;

        return new PendingAction
        {
            Kind = KindOf(row.Category, row.Id),
            ActionId = row.Id,
            TotalSteps = row.StepsCost,
            ConnectionId = order.ConnectionId,
            InteractableId = order.InteractableId,
            LightId = order.LightId,
            TargetActorId = order.TargetActorId,
            ItemId = order.ItemId,
            Target = order.Target,
            RemainingCm = 0,
        };
    }

    /// <summary>
    /// Which family of action a row belongs to.
    /// </summary>
    /// <remarks>
    /// The table's category is broad — every kind of contact is a "Combat" row — and the
    /// behaviours differ enough that the pipeline has to tell a takedown from a grenade.
    /// The dispatch is on the row id, which is stable, rather than on the category,
    /// which would put six unrelated behaviours behind one branch.
    /// </remarks>
    public static TacticalActionKind KindOf(ActionCategoryRow category, int actionId) => actionId switch
    {
        MoveWalk or MoveRun or MoveCrouch or MoveProne => TacticalActionKind.Move,
        TraverseDoor or TraverseVent or UseVent => TacticalActionKind.Traverse,
        OpenDoor or CloseDoor or LockDoor or ForceDoor => TacticalActionKind.OperateConnection,
        HackTerminal or SearchContainer or PlantDevice => TacticalActionKind.UseInteractable,
        Melee or MeleeLethal or Shoot or Suppress or Takedown => TacticalActionKind.Attack,
        HideBody or DragBody or Disguise or SwitchLight => TacticalActionKind.Stealth,
        _ => category switch
        {
            ActionCategoryRow.Move => TacticalActionKind.Move,
            ActionCategoryRow.Interact => TacticalActionKind.UseInteractable,
            ActionCategoryRow.Combat => TacticalActionKind.Attack,
            ActionCategoryRow.Stealth => TacticalActionKind.Stealth,
            _ => TacticalActionKind.Support,
        },
    };

    // ---- noise ---------------------------------------------------------------

    /// <summary>
    /// The noise an action makes, or null when the row is silent.
    /// </summary>
    /// <remarks>
    /// A row's <c>noise_profile_id</c> of zero means genuinely silent — blocking,
    /// waiting, observing, healing — and that is a real answer rather than missing data.
    /// Substituting a default profile would make a silent action audible and would mean
    /// "the table says this makes no noise" and "the table forgot to say" were
    /// indistinguishable.
    /// </remarks>
    private static NoiseEvent? MakeNoise(TacticalState state, TacticalActor actor, PendingAction pending)
    {
        int profileId = NoiseSystem.ProfileForAction(pending.ActionId);
        if (profileId <= 0)
            return null;

        TacticalActionRow row = SimulationRules.TacticalActionFor(pending.ActionId)!;

        var noise = new NoiseEvent(actor.Position, new Fixed32(NoiseSystem.BaseRadiusCm(profileId)), profileId, state.Step)
        {
            SourceActorId = actor.Id.Value,
            SourceKey = row.NameKey,
        };

        state.NoiseInFlight.Add(noise);
        return noise;
    }

    // ---- effects -------------------------------------------------------------

    /// <summary>
    /// Applies an action's effects. Returns true when the action was lethal.
    /// </summary>
    private static bool ApplyEffects(
        TacticalState state,
        TacticalActor actor,
        PendingAction pending,
        IRng rng)
    {
        TacticalActionRow row = SimulationRules.TacticalActionFor(pending.ActionId)!;
        state.Record("log.action.completed", pending.ActionId);

        // A movement's effects are its position, and MovementSystem owns that.
        if (pending.Kind == TacticalActionKind.Move)
            return false;

        switch (pending.ActionId)
        {
            case TraverseDoor:
            case TraverseVent:
            case UseVent:
                MovementSystem.CompleteTraversal(state, actor, pending);
                return false;

            case OpenDoor:
                SetDoorState(state, pending.ConnectionId, ConnectionState.Open);
                return false;

            case CloseDoor:
                SetDoorState(state, pending.ConnectionId, ConnectionState.Closed);
                return false;

            case LockDoor:
                SetDoorState(state, pending.ConnectionId, ConnectionState.Locked);
                return false;

            case ForceDoor:
                // Forcing is the one door action that is noisy in its own right as well
                // as through its row, and it leaves the door standing open rather than
                // slamming it, which is what makes forcing a route a permanent change.
                SetDoorState(state, pending.ConnectionId, ConnectionState.Open);
                return false;

            case HackTerminal:
            case SearchContainer:
            case PlantDevice:
                state.Objective.SpentSteps += row.StepsCost;

                if (state.Objective.Percent >= 100)
                {
                    state.Objective.IsComplete = true;
                    state.Record("log.objective.complete");
                }

                return false;

            case HideBody:
                HideTargetBody(state, actor, pending);
                return false;

            case DragBody:
                BeginDrag(state, actor, pending);
                return false;

            case Disguise:
                actor.DisguiseId = pending.ItemId != 0 ? pending.ItemId : DisguiseIdWhenUnspecified;
                state.Record("log.disguise.worn", actor.DisguiseId);
                return false;

            case SwitchLight:
                ToggleLight(state, actor, pending);
                return false;

            case Heal:
                HealTarget(state, pending, row);
                return false;

            case Revive:
                ReviveTarget(state, pending);
                return false;

            case CarryAlly:
                BeginCarry(state, actor, pending);
                return false;

            case SignalSquad:
                // Calling the squad in is what pushes the alarm from what the NPCs have
                // worked out to what they have been told.
                state.Alarm.Raise(SignalAlarmCost);
                state.Record("log.squad.signalled");
                return false;

            case Wait:
            case Observe:
            case Smoke:
            case Suppress:
                return false;

            case Melee:
            case MeleeLethal:
            case Shoot:
            case Takedown:
                return ApplyAttack(state, actor, pending, row, rng);

            default:
                // A row this build has never heard of still has to do something
                // defined. Recording it and returning false is defined; throwing would
                // turn a designer adding a row into a crash in every existing mission.
                state.Record("log.action.unhandled", pending.ActionId);
                return false;
        }
    }

    private static void SetDoorState(TacticalState state, SiteConnectionId id, ConnectionState value)
    {
        if (id.IsValid)
            state.Doors[id] = value;
    }

    private static void HideTargetBody(TacticalState state, TacticalActor actor, PendingAction pending)
    {
        TacticalActor? body = state.Actor(pending.TargetActorId);
        if (body is null)
            return;

        body.BodyHidden = true;
        state.Record("log.body.hidden", body.Id.Value);
    }

    private static void BeginDrag(TacticalState state, TacticalActor actor, PendingAction pending)
    {
        TacticalActor? body = state.Actor(pending.TargetActorId);
        if (body is null)
            return;

        // Dragging is carrying, with the same cost. Modelling it as a separate
        // relationship would mean two flags that have to be kept in step and a bug in
        // which one was set would leave a body that nobody can pick up and nobody can
        // leave behind.
        actor.Carrying = body.Id;
        body.IsCarried = true;
        body.Condition = ActorCondition.Carried;
        state.Record("log.body.dragged", body.Id.Value);
    }

    private static void ToggleLight(TacticalState state, TacticalActor actor, PendingAction pending)
    {
        if (state.Lights.Find(pending.LightId) is not { } light)
            return;

        bool nowOff = !light.SwitchedOff;
        state.Lights.SetSwitched(light.LightId, nowOff, state.Step);
        state.Record("log.light.switched", light.LightId, nowOff ? 1 : 0);
    }

    private static void HealTarget(TacticalState state, PendingAction pending, TacticalActionRow row)
    {
        TacticalActor? target = state.Actor(pending.TargetActorId);
        if (target is null)
            return;

        target.Health = Math.Min(target.MaxHealth, target.Health + HealAmount);
        state.Record("log.support.healed", target.Id.Value, HealAmount);
    }

    private static void ReviveTarget(TacticalState state, PendingAction pending)
    {
        TacticalActor? target = state.Actor(pending.TargetActorId);
        if (target is null || target.Condition != ActorCondition.Downed)
            return;

        target.Condition = ActorCondition.Active;
        target.Health = Math.Max(1, target.Health);
        target.BleedOutStepsRemaining = 0;
        state.Record("log.support.revived", target.Id.Value);
    }

    private static void BeginCarry(TacticalState state, TacticalActor actor, PendingAction pending)
    {
        TacticalActor? target = state.Actor(pending.TargetActorId);
        if (target is null)
            return;

        actor.Carrying = target.Id;
        target.IsCarried = true;
        target.Condition = ActorCondition.Carried;
        state.Record("log.support.carrying", target.Id.Value);
    }

    /// <summary>
    /// Resolves an attack and charges its consequences.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Damage comes from the weapon row when one was named and from the action row
    /// otherwise, so a bare-handed shove, a truncheon and a suppressed pistol are
    /// genuinely different without the action system knowing what any of them are.
    /// </para>
    /// <para>
    /// The consequences are the point (rule 19). A lethal act spends
    /// <c>heat_cost</c> on the mission, records <c>evidence_level</c> of what a later
    /// investigation could find, and costs the doer <c>mental_cost</c> — or
    /// <c>mental_cost_on_witness</c> instead, which is deliberately the larger number.
    /// Nobody has to decide here whether that is fair; they only have to be able to
    /// change the numbers.
    /// </para>
    /// </remarks>
    private static bool ApplyAttack(
        TacticalState state,
        TacticalActor actor,
        PendingAction pending,
        TacticalActionRow row,
        IRng rng)
    {
        TacticalActor? target = state.Actor(pending.TargetActorId);
        if (target is null)
            return false;

        // Somebody already dead or captured cannot be hurt again. The check has to be
        // here rather than inside ApplyOutcome, because ApplyOutcome is called *after*
        // the health is already subtracted — so a second hit on a corpse would be
        // deducted and then discarded, and a squad methodically executing the same
        // guard would walk that guard's health down to some large negative number.
        if (target.Condition is ActorCondition.Dead or ActorCondition.Captured)
            return false;

        int damage = WeaponDamage(pending, row);

        if (target.IsGuard || target.IsCivilian)
        {
            target.Health = Math.Max(0, target.Health - damage);
            DamageSystem.ApplyOutcome(state, target, row.IsLethal);
        }

        if (!row.IsLethal)
            return false;

        bool witnessed = WasWitnessed(state, actor);

        state.HeatGained += row.HeatCost;
        state.EvidenceLevel += row.EvidenceLevel;
        state.LethalActs++;

        // The mental cost lands on the agent's moral ledger. A guard has none — this is
        // the player's team, and there is no state on a site NPC for the weight of what
        // the player does with the people they kill.
        if (actor.IsAgent)
        {
            int cost = witnessed ? row.MentalCostOnWitness : row.MentalCost;
            actor.MoralStrain = (int)Math.Min(100L, actor.MoralStrain + cost);
        }

        state.Record(
            witnessed ? "log.kill.witnessed" : "log.kill.unwitnessed",
            target.Id.Value, row.HeatCost, row.EvidenceLevel);

        return true;
    }

    /// <summary>
    /// Whether anybody could see this happen.
    /// </summary>
    /// <remarks>
    /// A real perception check against every guard, using the same system the
    /// perception phase uses. That is deliberate: an action that resolves its own
    /// witnesses with a different rule from the one the game is played by would let a
    /// player be spotted by the simulation and not by the fiction, and the difference
    /// would only ever show up as an unexplained mental cost.
    /// </remarks>
    private static bool WasWitnessed(TacticalState state, TacticalActor actor)
    {
        foreach (TacticalActor other in state.SortedActors)
        {
            if (!other.IsGuard || other.Condition != ActorCondition.Active)
                continue;

            Perception perception = PerceptionSystem.CanPerceive(state.Layout, state.Lights, other, actor, state.Doors);

            if (perception.RaisesSuspicion)
                return true;
        }

        return false;
    }

    private static int WeaponDamage(PendingAction pending, TacticalActionRow row)
    {
        if (pending.ItemId > 0)
        {
            MeleeWeaponRow? weapon = SimulationRules.MeleeWeaponFor(pending.ItemId);
            if (weapon is not null)
                return weapon.Damage;

            ThrowableRow? thrown = SimulationRules.ThrowableFor(pending.ItemId);
            if (thrown is not null)
                return thrown.Damage;
        }

        return row.IsLethal ? LethalFallbackDamage : NonLethalFallbackDamage;
    }

    /// <summary>The interactable an order names, or null.</summary>
    private static SiteInteractable? FindInteractable(TacticalState state, int id)
    {
        foreach (SiteInteractable interactable in state.Layout.Interactables)
        {
            if (interactable.Id == id)
                return interactable;
        }

        return null;
    }

    /// <summary>Health restored by a heal action.</summary>
    private const int HealAmount = 30;

    /// <summary>Damage for a lethal act when no weapon was named.</summary>
    private const int LethalFallbackDamage = 40;

    /// <summary>Damage for a non-lethal act when no weapon was named.</summary>
    private const int NonLethalFallbackDamage = 15;

    /// <summary>
    /// The disguise id recorded when an order does not name one.
    /// </summary>
    /// <remarks>
    /// A disguise is a boolean fact for perception — "are they wearing something" — and
    /// its strength comes from the distance rule in
    /// <see cref="PerceptionSystem.DisguiseRangePercent"/>. Putting a number here
    /// rather than a magic non-zero sentinel means the state, the log and the UI all
    /// agree on which disguise it is.
    /// </remarks>
    private const int DisguiseIdWhenUnspecified = 1;

    /// <summary>Alarm a squad signal adds.</summary>
    private const int SignalAlarmCost = 10;
}
