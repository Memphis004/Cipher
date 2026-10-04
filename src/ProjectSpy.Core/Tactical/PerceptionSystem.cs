using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// One input to a perception calculation, and what it contributed.
/// </summary>
/// <remarks>
/// <para>
/// The brief is unambiguous that the breakdown is mandatory: a perception that returns
/// <see cref="PerceptionLevel.None"/> and no explanation is a system the player cannot
/// play, because "the guard did not see me" is a question they will ask on every single
/// step and the answer is usually one specific thing. These terms are that answer.
/// </para>
/// <para>
/// <b>Terms, not a sentence.</b> Each carries a factor, a value, a unit and a
/// localization key; Presentation assembles the sentence. Core writes no English
/// (knowledge.md rule 4), and the same terms serve the HUD, the debrief and the
/// headless balance harness without any of them re-deriving the formula.
/// </para>
/// <para>
/// <b>The terms are the actual arithmetic.</b> They are not a log written after the
/// fact — <see cref="PerceptionSystem"/> builds the same list it applies, so a term can
/// never disagree with the number it explains.
/// </para>
/// </remarks>
/// <param name="Factor">Which input this was.</param>
/// <param name="Unit">What the value is measured in.</param>
/// <param name="Value">The contribution, in <paramref name="Unit"/>.</param>
/// <param name="Key">Localization key for the factor's name.</param>
public readonly record struct PerceptionTerm(
    PerceptionFactor Factor,
    PerceptionTermUnit Unit,
    int Value,
    string Key);

/// <summary>Which input a perception term describes.</summary>
public enum PerceptionFactor
{
    /// <summary>The observer's raw sight range, from their archetype or skills.</summary>
    BaseRange = 0,

    /// <summary>The range bonus from a skill or an effect.</summary>
    RangeBonus = 1,

    /// <summary>How bright the target's surroundings are.</summary>
    Light = 2,

    /// <summary>How detectable the target's posture is.</summary>
    Posture = 3,

    /// <summary>Whether the target is moving conspicuously.</summary>
    Movement = 4,

    /// <summary>Whether the target is wearing something that misrepresents them.</summary>
    Disguise = 5,

    /// <summary>How far away the target actually is.</summary>
    Distance = 6,

    /// <summary>Whether the target is in front of the observer.</summary>
    Cone = 7,

    /// <summary>How much stood between them.</summary>
    Occlusion = 8,
}

/// <summary>What a <see cref="PerceptionTerm.Value"/> is measured in.</summary>
/// <remarks>
/// Declared rather than inferred from the factor, because the units genuinely differ:
/// a range is centimetres, a multiplier is a percentage, an occlusion is a count and a
/// cone test is a flag. A breakdown where the consumer had to know which was which would
/// be a breakdown that a UI layer eventually reads wrong.
/// </remarks>
public enum PerceptionTermUnit
{
    /// <summary>A distance in centimetres.</summary>
    Centimetres = 0,

    /// <summary>A percentage.</summary>
    Percent = 1,

    /// <summary>A whole number of things.</summary>
    Count = 2,

    /// <summary>Yes or no, as 1 or 0.</summary>
    Flag = 3,
}

/// <summary>
/// The answer to "can this observer make anything out of this target right now".
/// </summary>
/// <remarks>
/// <para>
/// A value rather than a bool, because the brief asks for three levels and a bool
/// cannot carry them. The levels are ordered and every downgrade is a discrete
/// transition with its own consequence: <see cref="PerceptionLevel.Noticed"/> makes a
/// guard stop and look, <see cref="PerceptionLevel.Identified"/> makes one pursue.
/// A guard that jumped from nothing to certain would make the game unwinnable by
/// accident, and a bool would force exactly that jump.
/// </para>
/// <para>
/// <b>The breakdown is always present</b>, including on a success, where it is what
/// tells the player how close it was to being missed. A breakdown that only appears on
/// failure teaches nothing about how to avoid the next one.
/// </para>
/// </remarks>
/// <param name="ObserverId">Who was looking.</param>
/// <param name="TargetId">Who was being looked at.</param>
/// <param name="Level">What they managed to make out.</param>
/// <param name="Blocker">Why nothing was perceived, when nothing was.</param>
/// <param name="DistanceCm">How far apart they were.</param>
/// <param name="EffectiveRangeCm">
/// How far the observer could have seen this target as things stood, after light,
/// posture, movement and disguise.
/// </param>
/// <param name="Terms">Every input and its contribution. Never empty.</param>
public readonly record struct Perception(
    TacticalActorId ObserverId,
    TacticalActorId TargetId,
    PerceptionLevel Level,
    PerceptionBlocker Blocker,
    Fixed32 DistanceCm,
    Fixed32 EffectiveRangeCm,
    IReadOnlyList<PerceptionTerm> Terms)
{
    /// <summary>True when nothing at all was perceived.</summary>
    public bool SawNothing => Level == PerceptionLevel.None;

    /// <summary>True when the observer can act on what they saw.</summary>
    public bool CanActOnIt => Level == PerceptionLevel.Identified;

    /// <summary>
    /// True when this perception is worth raising suspicion for.
    /// </summary>
    /// <remarks>
    /// <see cref="PerceptionLevel.Noticed"/> counts. A guard who can see a shape but not
    /// a face should still get suspicious, and a suspicion system that only fired on
    /// identification would mean a player could walk through a torchlit corridor in
    /// plain sight by hiding their badge.
    /// </remarks>
    public bool RaisesSuspicion => Level >= PerceptionLevel.Noticed;

    /// <summary>The breakdown rendered as a localization-key list, for the UI.</summary>
    public IReadOnlyList<string> TermKeys
    {
        get
        {
            var keys = new List<string>(Terms.Count);
            foreach (PerceptionTerm term in Terms)
                keys.Add(term.Key);

            return keys;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{ObserverId} sees {TargetId}: {Level}{(Blocker == PerceptionBlocker.None ? string.Empty : $" ({Blocker})")}";
}

/// <summary>
/// Decides what an observer can make out of a target, and says why.
/// </summary>
/// <remarks>
/// <para>
/// Core is the sole authority on this (knowledge.md rule 12: "lighting is gameplay, Core
/// is always the authority on can X perceive Y; Unity only renders what that computation
/// already decided"). Nothing here asks a render layer to agree.
/// </para>
/// <para>
/// <b>Everything is an interval on one line.</b> Rule 15 describes vision as a
/// perceiver covering <c>[x - range, x + range]</c> on its floor, clipped by occluders
/// and modified by light, posture and movement. This is that, in fixed-point centimetres,
/// with no trigonometry and no float anywhere — which is both what rule 6 demands and
/// what makes the result printable to the player as an exact distance.
/// </para>
/// <para>
/// <b>Order of operations.</b> Range is extended by skill, then cut by what the target
/// looks like (light, posture, disguise), then extended again by conspicuous movement,
/// then compared to the distance. Concealment is applied as a multiplier on range
/// rather than as a threshold on distance, so "prone in a lit room" and "walking in a
/// dark one" can be compared by the player using the same arithmetic — and because
/// every term in the breakdown is the number actually used, they can.
/// </para>
/// </remarks>
public static class PerceptionSystem
{
    /// <summary>
    /// What fraction of the notice range an observer must be inside to identify rather
    /// than merely notice.
    /// </summary>
    /// <remarks>
    /// Structural. The gap between "there is someone there" and "that is the
    /// infiltrator" is the margin a stealth player is managing, and making it a fixed
    /// proportion of whatever range they have — rather than a fixed number of centimetres
    /// — is what keeps it meaningful for an Infiltration-20 operative and an
    /// Infiltration-90 one alike.
    /// </remarks>
    public const int IdentifyRangePercent = 60;

    /// <summary>
    /// Fraction of notice range available in an unlit room.
    /// </summary>
    /// <remarks>
    /// Zero, and deliberately so — see <see cref="LightRangePercent"/>. Kept as a named
    /// constant rather than folded into the switch because it is the single number the
    /// lights-out tactic turns on, and a designer retuning it should have it findable.
    /// </remarks>
    public const int DarkRangePercent = 0;

    /// <summary>Fraction of notice range available in a dimly lit room.</summary>
    public const int DimRangePercent = 70;

    /// <summary>Fraction of notice range available in a fully lit room.</summary>
    public const int LitRangePercent = 100;

    /// <summary>
    /// Extra range, in percent, when the target is moving conspicuously.
    /// </summary>
    /// <remarks>
    /// A bonus rather than a penalty, and the sign is the point: a running target is
    /// not harder to see for being quick, it is <em>easier</em> — it is moving, which
    /// is what the eye picks up. A crouching one gets nothing. That is the whole reason
    /// posture exists, and reversing it would make sprinting strictly worse for
    /// stealth, which is both untrue and makes the choice trivial.
    /// </remarks>
    public const int MovementRangeBonusPercent = 25;

    /// <summary>
    /// Fraction of range a disguise leaves available to a distant observer.
    /// </summary>
    /// <remarks>
    /// A disguise is not invisibility. Someone close enough, or attentive enough, still
    /// gets an identification; someone across the room does not. At 45% of range, a
    /// disguised operative can cross a lit room past a sentry at 1800 cm and still be
    /// identified at close quarters — which is the honest description of what wearing a
    /// uniform does.
    /// </remarks>
    public const int DisguiseRangePercent = 45;

    /// <summary>
    /// Works out what <paramref name="observer"/> can make out of
    /// <paramref name="target"/> right now.
    /// </summary>
    /// <param name="layout">The building, for occlusion and doors.</param>
    /// <param name="light">Current lighting.</param>
    /// <param name="observer">The one looking.</param>
    /// <param name="target">The one being looked at.</param>
    /// <param name="doors">
    /// The doors' live states, when there are any. Optional so that a caller with no
    /// mission in hand — a table check, a design-time query — still gets an answer from
    /// the door <em>types</em> alone. Within a mission this is always passed, because
    /// without it every closed door looks exactly like an open one and the stealth
    /// system stops working.
    /// </param>
    /// <returns>
    /// A graded result that always carries its breakdown. Never null and never empty.
    /// </returns>
    public static Perception CanPerceive(
        SiteLayout layout,
        LightState light,
        TacticalActor observer,
        TacticalActor target,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doors = null)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));
        if (light is null) throw new ArgumentNullException(nameof(light));
        if (observer is null) throw new ArgumentNullException(nameof(observer));
        if (target is null) throw new ArgumentNullException(nameof(target));

        var terms = new List<PerceptionTerm>(8);

        // ---- geometry ---------------------------------------------------------
        bool sameFloor = observer.Position.IsOnSameFloorAs(target.Position);
        Fixed32 distance = sameFloor
            ? Fixed32.Distance(observer.Position.X, target.Position.X)
            : new Fixed32(CrossFloorDistanceCm(layout, observer, target));

        terms.Add(new PerceptionTerm(
            PerceptionFactor.Distance, PerceptionTermUnit.Centimetres, distance.Raw, "perception.factor.distance"));

        // ---- the observer's own reach -----------------------------------------
        int baseRange = observer.Vision.VisionRangeCm;
        terms.Add(new PerceptionTerm(
            PerceptionFactor.BaseRange, PerceptionTermUnit.Centimetres, baseRange, "perception.factor.baseRange"));

        int range = baseRange + SimulationRules.PercentOf(baseRange, observer.Vision.RangeBonusPercent);
        terms.Add(new PerceptionTerm(
            PerceptionFactor.RangeBonus, PerceptionTermUnit.Percent,
            observer.Vision.RangeBonusPercent, "perception.factor.rangeBonus"));

        // ---- the cone ---------------------------------------------------------
        bool behind = IsBehind(observer, target);
        bool inCone = !behind || observer.Vision.SeesBehind;

        terms.Add(new PerceptionTerm(
            PerceptionFactor.Cone, PerceptionTermUnit.Flag, inCone ? 1 : 0, "perception.factor.cone"));

        if (!inCone)
        {
            return new Perception(observer.Id, target.Id, PerceptionLevel.None,
                PerceptionBlocker.OutsideCone, distance, new Fixed32(range), terms);
        }

        // ---- what stands between them -----------------------------------------
        // Occluders cut the sightline rather than merely being counted. Rule 15 says a
        // perceiver covers an interval "clipped by occluders", and on a one-dimensional
        // line anything standing strictly between two actors ends the view: there is no
        // angle to look around a filing cabinet from. Counting them without honouring
        // them would have made an occluded sightline identical to a clear one.
        int occluders = CountOccluders(layout, observer, target, doors);

        terms.Add(new PerceptionTerm(
            PerceptionFactor.Occlusion, PerceptionTermUnit.Count, occluders, "perception.factor.occlusion"));

        // ---- how visible the target is ----------------------------------------
        // Every term is computed before any of them can decide the outcome, so the
        // breakdown is complete whatever the answer. A breakdown that stopped at the
        // first gate would explain some failures and not others, and the player would
        // have no way to tell which.
        SiteLightLevel level = light.LevelAt(layout, target.Position);
        int lightPercent = LightRangePercent(level);

        terms.Add(new PerceptionTerm(
            PerceptionFactor.Light, PerceptionTermUnit.Percent, lightPercent, "perception.factor.light"));

        int detection = PostureRules.DetectionPercent(target.Posture);
        terms.Add(new PerceptionTerm(
            PerceptionFactor.Posture, PerceptionTermUnit.Percent, detection, "perception.factor.posture"));

        range = SimulationRules.PercentOf(SimulationRules.PercentOf(range, lightPercent), detection);

        bool disguised = target.DisguiseId != 0;
        if (disguised)
        {
            terms.Add(new PerceptionTerm(
                PerceptionFactor.Disguise, PerceptionTermUnit.Percent,
                DisguiseRangePercent, "perception.factor.disguise"));

            range = SimulationRules.PercentOf(range, DisguiseRangePercent);
        }

        if (PostureRules.IsConspicuous(target.Posture))
        {
            terms.Add(new PerceptionTerm(
                PerceptionFactor.Movement, PerceptionTermUnit.Percent,
                MovementRangeBonusPercent, "perception.factor.movement"));

            range += SimulationRules.PercentOf(range, MovementRangeBonusPercent);
        }

        // ---- the comparison ---------------------------------------------------
        // In this order, and the order is the explanation: something in the way beats
        // being too far away, and being in the dark beats everything. Each gate reports
        // which one it was, because "the guard did not see me" is a question the player
        // asks on every step and "you were behind a door" and "you were too far" are
        // different answers.
        if (occluders > 0)
        {
            return new Perception(observer.Id, target.Id, PerceptionLevel.None,
                PerceptionBlocker.Occluded, distance, new Fixed32(range), terms);
        }

        if (lightPercent <= 0)
        {
            return new Perception(observer.Id, target.Id, PerceptionLevel.None,
                PerceptionBlocker.Unlit, distance, new Fixed32(0), terms);
        }

        if (distance.Raw > range)
        {
            return new Perception(observer.Id, target.Id, PerceptionLevel.None,
                PerceptionBlocker.OutOfRange, distance, new Fixed32(range), terms);
        }

        int identifyAt = SimulationRules.PercentOf(range, IdentifyRangePercent);
        bool identified = distance.Raw <= identifyAt;

        // A disguise does not stop an identification at close quarters, but it does stop
        // a silhouette from becoming a name — so a disguised target cannot be identified
        // past the disguise's own range even when the maths would otherwise allow it.
        if (identified && disguised && distance.Raw > SimulationRules.PercentOf(range, DisguiseRangePercent))
            identified = false;

        PerceptionLevel result = identified ? PerceptionLevel.Identified : PerceptionLevel.Noticed;

        return new Perception(observer.Id, target.Id, result, PerceptionBlocker.None,
            distance, new Fixed32(range), terms);
    }

    /// <summary>
    /// Feeds a perception into the observer's suspicion and memory, and returns how much
    /// suspicion moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gain is the archetype's <c>suspicion_gain_rate</c>, scaled by how close the
    /// perception was to the edge of the observer's range. A shape at the very limit of
    /// vision is barely noticed; the same shape filling the corridor is unmistakable,
    /// and a system that charged the same for both would make distance the only thing
    /// that matters.
    /// </para>
    /// <para>
    /// Civilians are excluded. A member of the public does not develop a suspicion of
    /// infiltrators; they react, which is a stage-4d behaviour. Charging them suspicion
    /// would make "somebody saw me" and "somebody recognised me" the same event, and
    /// would turn the stealth system into a noise-avoidance system.
    /// </para>
    /// </remarks>
    public static int Apply(
        Perception perception,
        IReadOnlyDictionary<int, TacticalActor> actorsById,
        int gainRatePerStep,
        long step)
    {
        if (!perception.RaisesSuspicion)
            return 0;

        if (!actorsById.TryGetValue(perception.ObserverId.Value, out TacticalActor? observer))
            return 0;

        if (observer.IsCivilian)
            return 0;

        observer.Memory.NotePerception(
            perception.TargetId,
            perception.TargetId.Value == 0 ? TacticalPosition.None : TargetPositionOf(actorsById, perception.TargetId),
            perception.Level,
            step);

        if (perception.EffectiveRangeCm.Raw <= 0)
            return 0;

        // How much of the available range the target was inside. 100 when it is right in
        // front of them, near zero at the very edge of vision.
        int closenessPercent = (int)((long)perception.DistanceCm.Raw * 100L / perception.EffectiveRangeCm.Raw);
        closenessPercent = closenessPercent > 100 ? 100 : closenessPercent;

        int gain = SimulationRules.PercentOf(gainRatePerStep, 100 - closenessPercent);

        // Only what they actually saw, not what they could have seen. A notice is
        // worth about a fifth of an identification, which is what makes staying dark a
        // viable tactic rather than merely a slower one.
        if (perception.Level == PerceptionLevel.Noticed)
            gain = SimulationRules.PercentOf(gain, 20);

        if (gain <= 0)
            return 0;

        observer.Suspicion.Add(gain);
        return gain;
    }

    /// <summary>
    /// Records what <paramref name="observer"/> can see of <paramref name="target"/>
    /// without letting it change anything about the site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Apply"/> does two jobs at once: it writes to the observer's
    /// <see cref="PerceptionMemory"/> and it feeds the observer's suspicion meter. For a
    /// guard those belong together — a guard who saw something is a guard who is now
    /// suspicious of you. For the player's own operatives they do not, and running them
    /// through <see cref="Apply"/> would have the squad spy on the building and start
    /// the alarm over it.
    /// </para>
    /// <para>
    /// <b>This is the player's only source of guard positions</b>, and it has to be the
    /// same arithmetic the guards use against the squad. Symmetry is the point: if the
    /// player's eyes were computed differently from the guards', the fog of war would be
    /// showing the player a world that does not obey the game's own rules, and every
    /// decision made against what is on screen would be made against a lie.
    /// </para>
    /// <para>
    /// Returns whether anything was recorded, so a caller can distinguish "saw
    /// nothing" from "there was nothing to see".
    /// </para>
    /// </remarks>
    public static bool RecordSighting(
        Perception perception,
        IReadOnlyDictionary<int, TacticalActor> actorsById,
        long step)
    {
        if (perception.SawNothing)
            return false;

        if (!actorsById.TryGetValue(perception.ObserverId.Value, out TacticalActor? observer))
            return false;

        observer.Memory.NotePerception(
            perception.TargetId,
            TargetPositionOf(actorsById, perception.TargetId),
            perception.Level,
            step);

        return true;
    }

    /// <summary>Fraction of notice range available at a light level.</summary>
    /// <remarks>
    /// <see cref="SiteLightLevel.Dark"/> is <em>zero</em>, and that is the whole stealth
    /// contract rather than a tuning choice: in a room with no usable light there is
    /// nothing to see, however close the observer is. A dark room that still lets a guard
    /// notice you at arm's length would make "turn the lights out" a suggestion, and the
    /// player would be right to distrust every other thing the perception system says.
    /// </remarks>
    public static int LightRangePercent(SiteLightLevel level) => level switch
    {
        SiteLightLevel.Dark => 0,
        SiteLightLevel.Dim => DimRangePercent,
        SiteLightLevel.Lit => LitRangePercent,
        _ => 0,
    };

    /// <summary>
    /// Whether the target is behind the observer, on the observer's own floor.
    /// </summary>
    /// <remarks>
    /// Cross-floor always counts as not behind, because there is no facing that grants
    /// a sightline through a floor. Treating it as behind would make an observer's cone
    /// a floor-swap toggle and a guard would "see" the basement whenever they were
    /// facing the wrong way upstairs, which is nonsense rather than a difficulty.
    /// </remarks>
    public static bool IsBehind(TacticalActor observer, TacticalActor target)
    {
        if (!observer.Position.IsOnSameFloorAs(target.Position))
            return false;

        bool targetIsLeft = target.Position.X.Raw < observer.Position.X.Raw;
        return observer.Facing == Facing.Right ? targetIsLeft : !targetIsLeft;
    }

    /// <summary>
    /// How many things stand between the observer and the target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts furniture on the floor between them and, when they are in different
    /// rooms, the connection between — a door that <c>blocks_vision</c> occludes
    /// absolutely, while a window does not. A room with twenty filing cabinets between
    /// you and a doorway is not a room you can be seen across.
    /// </para>
    /// <para>
    /// The count rather than a yes/no is deliberate: the breakdown shows it, so the
    /// player learns that crouching behind <em>one</em> cabinet works and that they
    /// moved past three.
    /// </para>
    /// <para>
    /// <b>A door's live state beats its type.</b> <c>blocks_vision</c> says what a
    /// door is; it does not say whether the door is currently shut. Reading only the
    /// type made opening a door useless — the sightline through it stayed blocked, so
    /// the single most basic stealth decision in the game, closing the door behind you,
    /// changed nothing at all. <see cref="ConnectionState"/>'s own documentation already
    /// promised the other behaviour ("blocks sight and sound <em>until opened</em>");
    /// this is the code catching up with it.
    /// </para>
    /// </remarks>
    public static int CountOccluders(
        SiteLayout layout,
        TacticalActor observer,
        TacticalActor target,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doors = null)
    {
        SiteRoom? observerRoom = layout.RoomContaining(observer.Position);
        SiteRoom? targetRoom = layout.RoomContaining(target.Position);

        if (observerRoom is null || targetRoom is null)
            return 1;

        if (observerRoom.Id != targetRoom.Id)
            return CountBlockingDoors(layout, observerRoom, targetRoom, doors);

        Fixed32 low = Fixed32.Min(observer.Position.X, target.Position.X);
        Fixed32 high = Fixed32.Max(observer.Position.X, target.Position.X);

        int count = 0;

        foreach (SiteOccluder occluder in layout.Occluders)
        {
            if (occluder.RoomId != observerRoom.Id)
                continue;

            // Strictly between: standing behind a cabinet is exactly what the cabinet
            // is for, and a half-open test at the observer's own x would make the thing
            // they are hiding behind block them anyway.
            if (occluder.X > low && occluder.X < high)
                count++;
        }

        return count;
    }

    /// <summary>Connections between two rooms that stop the sightline going through.</summary>
    private static int CountBlockingDoors(
        SiteLayout layout,
        SiteRoom from,
        SiteRoom to,
        IReadOnlyDictionary<SiteConnectionId, ConnectionState>? doors)
    {
        foreach (SiteConnection connection in layout.ConnectionsAt(from.Id))
        {
            if (connection.Other(from.Id) != to.Id)
                continue;

            // A vertical connection is a stairwell or a vent: there is no straight line
            // through it to look down, so it neither occludes nor transmits sight.
            if (connection.IsVertical)
                return 0;

            // Standing open, it is a hole in the wall whatever it is made of. This is
            // checked first so that an interior window that happens to be closed also
            // stops the sightline, which is what a closed window does.
            if (doors is not null
                && doors.TryGetValue(connection.Id, out ConnectionState live)
                && live == ConnectionState.Open)
            {
                return 0;
            }

            // One and zero are both real answers here and the difference matters: a door
            // that does not block vision is an interior window, and two rooms joined by
            // one can see into each other.
            return connection.BlocksVision ? 1 : 0;
        }

        // No direct connection at all: the rooms are not in line of sight from each
        // other, which on a cutaway building means a wall and a corner.
        return 1;
    }

    /// <summary>
    /// A distance for two actors on different floors.
    /// </summary>
    /// <remarks>
    /// Not a real distance — there is no single number for "how far apart" on a 1-D
    /// per-floor model. It is the greater of the two horizontal separations plus a
    /// structural penalty per floor, which is enough for a range comparison and honest
    /// about not being a straight line. Anything that needs a true cross-floor distance
    /// must route through <see cref="Pathfinder"/>.
    /// </remarks>
    private static int CrossFloorDistanceCm(SiteLayout layout, TacticalActor observer, TacticalActor target)
    {
        SiteRoom? observerRoom = layout.RoomContaining(observer.Position);
        SiteRoom? targetRoom = layout.RoomContaining(target.Position);

        if (observerRoom is null || targetRoom is null)
            return int.MaxValue;

        long floorDelta = Math.Abs((long)observerRoom.FloorIndex - targetRoom.FloorIndex);
        long horizontal = Math.Abs((long)observer.Position.X.Raw - target.Position.X.Raw);

        return (int)(horizontal + (floorDelta * FloorCrossingPenaltyCm));
    }

    /// <summary>
    /// Centimetres a floor change is worth when comparing across floors.
    /// </summary>
    /// <remarks>
    /// Structural, and equal to a typical ground-floor room's span rather than to
    /// nothing. A guard one floor away should still be able to notice somebody moving
    /// about on the other side of a stairwell — that is what makes a basement a refuge
    /// rather than an invulnerable pocket — but it should cost enough that they cannot
    /// watch a whole floor from the stair foot.
    /// </remarks>
    private const int FloorCrossingPenaltyCm = 1200;

    /// <summary>The target's position, or none when the target no longer exists.</summary>
    private static TacticalPosition TargetPositionOf(
        IReadOnlyDictionary<int, TacticalActor> actorsById,
        TacticalActorId targetId)
        => actorsById.TryGetValue(targetId.Value, out TacticalActor? target)
            ? target.Position
            : TacticalPosition.None;
}
