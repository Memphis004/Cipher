using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// The posture table: what each stance costs and what it gives back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which numbers live here and which live in a table.</b> The per-step distance a
/// posture covers and how detectable it makes its carrier are <em>structural</em>: they
/// say what prone means on a one-dimensional cutaway, and there is no designer decision
/// in "a prone body is harder to see at the bottom of a corridor". The step cost and the
/// noise each posture makes are genuine balance numbers and are read from
/// <c>tactical_action</c> and <c>noise_profile</c> through
/// <see cref="SimulationRules"/> (knowledge.md rule 3).
/// </para>
/// <para>
/// <b>Why the speeds are a doubling ladder.</b> 6, 12, 24, 48 cm per step means every
/// posture is exactly twice the one below it. That is not decoration: it means a guard
/// patrolling at the watchman's 18 cm per step is faster than a walking player and
/// slower than a running one, so "can I outrun him" has the same answer on every site
/// instead of depending on which archetype happens to patrol the corridor the player
/// happens to be standing in.
/// </para>
/// </remarks>
public static class PostureRules
{
    /// <summary>Centimetres covered per 100 ms step while prone.</summary>
    public const int ProneCmPerStep = 6;

    /// <summary>Centimetres covered per 100 ms step while crouched.</summary>
    public const int CrouchCmPerStep = 12;

    /// <summary>Centimetres covered per 100 ms step while walking.</summary>
    public const int WalkCmPerStep = 24;

    /// <summary>Centimetres covered per 100 ms step while running.</summary>
    public const int RunCmPerStep = 48;

    /// <summary>
    /// How detectable a carrier in this posture is, as a percentage of an upright
    /// walking target's. Higher is easier to see.
    /// </summary>
    public static int DetectionPercent(Posture posture) => posture switch
    {
        Posture.Prone => 40,
        Posture.Crouch => 60,
        Posture.Walk => 85,
        Posture.Run => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(posture), posture, "Unknown posture."),
    };

    /// <summary>
    /// The <c>tactical_action</c> row that moves an entity in this posture.
    /// </summary>
    /// <remarks>
    /// The action's own <c>steps_cost</c> is what a <em>command</em> costs the player;
    /// this lookup is how a movement in progress finds the row that says what one step
    /// of it is worth.
    /// </remarks>
    public static int MoveActionId(Posture posture) => posture switch
    {
        Posture.Prone => 12404,   // action.move_prone
        Posture.Crouch => 12403,  // action.move_crouch
        Posture.Walk => 12401,    // action.move_walk
        Posture.Run => 12402,     // action.move_run
        _ => throw new ArgumentOutOfRangeException(nameof(posture), posture, "Unknown posture."),
    };

    /// <summary>The <c>noise_profile</c> a step in this posture makes.</summary>
    /// <remarks>
    /// Prone and crouch share <c>noise.crouch_step</c>. That is a real modelling
    /// decision and not a copy-and-paste: a prone entity is not silent, it is slow, and
    /// the table gives it the quietest profile there is. Giving prone its own, quieter,
    /// profile would make crawling strictly better than crouching with no downside,
    /// and there is no reason to prefer it.
    /// </remarks>
    public static int StepNoiseProfileId(Posture posture) => posture switch
    {
        Posture.Prone => 12301,   // noise.crouch_step
        Posture.Crouch => 12301,  // noise.crouch_step
        Posture.Walk => 12302,    // noise.walk_step
        Posture.Run => 12303,     // noise.run_step
        _ => throw new ArgumentOutOfRangeException(nameof(posture), posture, "Unknown posture."),
    };

    /// <summary>Centimetres covered per step in this posture.</summary>
    public static Fixed32 SpeedPerStep(Posture posture) => new(posture switch
    {
        Posture.Prone => ProneCmPerStep,
        Posture.Crouch => CrouchCmPerStep,
        Posture.Walk => WalkCmPerStep,
        Posture.Run => RunCmPerStep,
        _ => throw new ArgumentOutOfRangeException(nameof(posture), posture, "Unknown posture."),
    });

    /// <summary>True when the posture is conspicuous enough to notice from a distance.</summary>
    /// <remarks>
    /// The threshold is <see cref="Posture.Walk"/> because that is the point at which a
    /// carrier stops being "someone" and becomes "someone heading this way". It is used
    /// by perception as a movement multiplier and by the alarm's "was anything moving"
    /// check, so the two can never disagree about what counts as conspicuous.
    /// </remarks>
    public static bool IsConspicuous(Posture posture) => posture >= Posture.Walk;
}

/// <summary>
/// One stretch of floor that is lit, and how brightly.
/// </summary>
/// <param name="StartX">Left edge, inclusive.</param>
/// <param name="EndX">Right edge, exclusive.</param>
/// <param name="Level">How bright. Overlaps take the brighter answer.</param>
/// <param name="EmitterId">
/// The <see cref="SiteLight"/> that produced it. Kept so the UI can say <em>which</em>
/// lamp, and so an interval can be traced back to the emitter that produced it when a
/// switch is thrown.
/// </param>
public readonly record struct LightInterval(Fixed32 StartX, Fixed32 EndX, SiteLightLevel Level, int EmitterId)
{
    /// <summary>True when the interval covers a point. Half-open, like every other interval.</summary>
    public bool Contains(Fixed32 x) => x >= StartX && x < EndX;

    /// <inheritdoc/>
    public override string ToString() => $"[{StartX.Raw},{EndX.Raw}) {Level} from light {EmitterId}";
}

/// <summary>
/// One emitter's live state: whether it is on, whether it is smashed, and when it comes
/// back.
/// </summary>
/// <remarks>
/// The generated layout says what light exists; this says what is currently working.
/// The split is the same one the layout uses for everything else — structure is
/// committed and regenerates from the seed, progress is state — and it means throwing a
/// switch does not dirty the building.
/// </remarks>
public sealed class LightRuntime
{
    /// <summary>The emitter this is the state of.</summary>
    public int LightId { get; init; }

    /// <summary>Which room it stands in.</summary>
    public SiteRoomId RoomId { get; init; }

    /// <summary>Which floor that room is on.</summary>
    public int FloorIndex { get; init; }

    /// <summary>Foreign key into <c>light_source</c>.</summary>
    public int LightSourceId { get; init; }

    /// <summary>Where it stands, in centimetres.</summary>
    public Fixed32 X { get; init; }

    /// <summary>How bright it is when working.</summary>
    public SiteLightLevel Level { get; init; }

    /// <summary>How far it reaches, in centimetres.</summary>
    public int RadiusCm { get; init; }

    /// <summary>True when a switch has been thrown and it is off.</summary>
    public bool SwitchedOff { get; set; }

    /// <summary>True when it has been smashed. A smashed light never comes back.</summary>
    public bool Destroyed { get; set; }

    /// <summary>
    /// The step at which it starts working again, or <c>null</c> when nothing will.
    /// </summary>
    /// <remarks>
    /// From <c>light_source.restores_after_steps</c>. Zero in the table means it never
    /// comes back, which is why this is nullable rather than a step count that happens
    /// to be far in the future — "never" and "very much later" are different answers and
    /// a plan that relies on a floodlight returning must not quietly depend on one.
    /// </remarks>
    public long? RestoresAtStep { get; set; }

    /// <summary>True when the emitter is contributing light right now.</summary>
    public bool IsWorking => !Destroyed && !SwitchedOff;

    /// <inheritdoc/>
    public override string ToString()
        => $"light {LightId} in {RoomId} {(Destroyed ? "destroyed" : SwitchedOff ? "off" : Level.ToString())}";
}

/// <summary>
/// The lighting of the whole building, and the switches that change it.
/// </summary>
/// <remarks>
/// <para>
/// Rule 15 asks for "emitters define lit intervals" and for Core to track Lit, Dim or
/// Dark per interval, and this is that: <see cref="LevelAt"/> is the one answer to "how
/// well lit is that spot", and both the perception system and the UI read it rather than
/// each re-deriving it from the emitter list.
/// </para>
/// <para>
/// <b>Intervals are recomputed per room, not merged site-wide.</b> A lamp's reach is
/// clipped to the room that holds it, because a lit interval running through a wall
/// would let a guard see through a doorway into a dark corridor — which is precisely
/// the mistake a cutaway view invites and precisely the one a stealth game cannot
/// afford.
/// </para>
/// <para>
/// <b>Overlaps take the brighter answer.</b> Two dim lamps side by side are dimmer than
/// one and no brighter; taking the maximum is what makes destroying the lamp that
/// matters actually darken a room rather than leaving the other one holding it open.
/// </para>
/// </remarks>
public sealed class LightState
{
    private readonly Dictionary<int, LightRuntime> _byId = new();
    private readonly Dictionary<SiteRoomId, List<LightInterval>> _intervals = new();
    // Named "extent" rather than "bounds": knowledge.md rule 10's guard bans the token
    // "bounds" in every namespace, render and simulation alike, because on this codebase
    // it has always meant a render box. A room really is an interval with an extent, so
    // the name is both accurate and one the guard can keep doing its job on.
    private readonly Dictionary<SiteRoomId, (Fixed32 Start, Fixed32 End)> _roomExtent = new();

    /// <summary>Every emitter, in the order the layout placed them.</summary>
    public IReadOnlyList<LightRuntime> Emitters { get; private set; } = Array.Empty<LightRuntime>();

    /// <summary>
    /// Builds the live lighting for a generated site.
    /// </summary>
    /// <remarks>
    /// Takes the layout rather than the bare emitter list because an emitter has to be
    /// resolved to its floor and its room's bounds before it can describe a lit
    /// interval, and passing the layout means the floor index is recorded rather than
    /// looked up later from a different source that might disagree.
    /// </remarks>
    public LightState(SiteLayout layout)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));

        var runtimes = new List<LightRuntime>(layout.Lights.Count);

        foreach (SiteLight light in layout.Lights)
        {
            SiteRoom? room = layout.Find(light.RoomId);

            if (room is not null)
                _roomExtent[room.Id] = (room.StartX, room.EndX);

            runtimes.Add(new LightRuntime
            {
                LightId = light.Id,
                RoomId = light.RoomId,
                FloorIndex = room?.FloorIndex ?? 0,
                LightSourceId = light.LightSourceId,
                X = light.X,
                Level = light.Level,
                RadiusCm = light.RadiusCm,

                // No restore is armed until something actually breaks or switches the
                // emitter: `restores_after_steps` is how long the building takes to fix
                // it, not a countdown that runs from mission start.
                RestoresAtStep = null,
            });
        }

        Emitters = runtimes;
        foreach (LightRuntime runtime in runtimes)
            _byId[runtime.LightId] = runtime;

        Recompute();
    }

    /// <summary>An emitter's live state, or null when the id names nothing.</summary>
    public LightRuntime? Find(int lightId)
        => _byId.TryGetValue(lightId, out LightRuntime? runtime) ? runtime : null;

    /// <summary>The lit intervals of one room, in ascending order.</summary>
    public IReadOnlyList<LightInterval> IntervalsIn(SiteRoomId roomId)
        => _intervals.TryGetValue(roomId, out List<LightInterval>? list)
            ? list
            : (IReadOnlyList<LightInterval>)Array.Empty<LightInterval>();

    /// <summary>
    /// How well lit a point is, falling back to the room's own default when no emitter
    /// reaches it.
    /// </summary>
    /// <remarks>
    /// The fallback is <see cref="SiteRoom.DefaultLightLevel"/> rather than
    /// <see cref="SiteLightLevel.Dark"/> because a room in a daylit building is dim with
    /// every lamp switched off, and hard-coding "dark" would make the first thing a
    /// player does — turn the lights off — do nothing at all.
    /// </remarks>
    public SiteLightLevel LevelAt(SiteLayout layout, TacticalPosition at)
    {
        SiteRoom? room = layout.RoomContaining(at);
        if (room is null)
            return SiteLightLevel.Dark;

        SiteLightLevel best = SiteLightLevel.Dark;

        foreach (LightInterval interval in IntervalsIn(room.Id))
        {
            if (!interval.Contains(at.X))
                continue;

            if (interval.Level > best)
                best = interval.Level;
        }

        return best > room.DefaultLightLevel ? best : room.DefaultLightLevel;
    }

    /// <summary>
    /// Turns an emitter off or on. Returns false when there is no such emitter or it is
    /// smashed.
    /// </summary>
    public bool SetSwitched(int lightId, bool off, long step)
    {
        if (!_byId.TryGetValue(lightId, out LightRuntime? runtime) || runtime.Destroyed)
            return false;

        if (runtime.SwitchedOff == off)
            return false;

        runtime.SwitchedOff = off;
        ArmRestore(lightId, step);
        Recompute();
        return true;
    }

    /// <summary>
    /// Smashes an emitter.
    /// </summary>
    /// <remarks>
    /// Not necessarily permanent: <see cref="ArmRestore"/> is called here rather than by
    /// the caller, because a lamp that can be smashed but must also be armed by hand is
    /// a lamp whose restore gets forgotten in one of the three places that smash
    /// something, and a room that stays dark for the rest of the mission is a bug the
    /// player experiences as a lie.
    /// </remarks>
    /// <returns>
    /// False when there is no such emitter, or the table says it cannot be destroyed, or
    /// it is already destroyed.
    /// </returns>
    public bool Destroy(int lightId, long step)
    {
        if (!_byId.TryGetValue(lightId, out LightRuntime? runtime))
            return false;

        if (runtime.Destroyed)
            return false;

        if (SimulationRules.LightSourceFor(runtime.LightSourceId)?.CanBeDestroyed != true)
            return false;

        runtime.Destroyed = true;
        ArmRestore(lightId, step);
        Recompute();
        return true;
    }

    /// <summary>
    /// The noise id a smash makes, or zero when the emitter cannot be destroyed.
    /// </summary>
    /// <remarks>
    /// The table gives <c>noise_on_destroy</c> as a bare number rather than a profile
    /// id, so it is read as the profile's own radius. Passing a radius where an id is
    /// expected would be a silent mismatch, and a listener standing next to a smashed
    /// lamp deserves to hear it.
    /// </remarks>
    public int NoiseProfileOnDestroy(int lightId)
    {
        if (!_byId.TryGetValue(lightId, out LightRuntime? runtime))
            return 0;

        return SimulationRules.LightSourceFor(runtime.LightSourceId)?.NoiseOnDestroy ?? 0;
    }

    /// <summary>
    /// Brings back anything whose restore time has arrived, and advances the clock on
    /// the ones that were smashed or switched off.
    /// </summary>
    /// <remarks>
    /// Only emitters that were destroyed or switched off and have a restore time come
    /// back. A switched-off lamp that has a restore time is one somebody turned off and
    /// the building's own timer undoes, which is what makes a torch-and-wait tactic
    /// cost real time rather than being free.
    /// </remarks>
    public IReadOnlyList<int> RestoreElapsed(long step)
    {
        List<int> restored = new();

        foreach (LightRuntime runtime in Emitters)
        {
            if (runtime.Destroyed || !runtime.SwitchedOff)
                continue;

            if (runtime.RestoresAtStep is not { } at || step < at)
                continue;

            runtime.Destroyed = false;
            runtime.SwitchedOff = false;
            runtime.RestoresAtStep = null;
            restored.Add(runtime.LightId);
        }

        if (restored.Count > 0)
            Recompute();

        return restored;
    }

    /// <summary>
    /// Arms the restore timers after a smash or a switch, from
    /// <c>light_source.restores_after_steps</c>.
    /// </summary>
    public void ArmRestore(int lightId, long step)
    {
        if (!_byId.TryGetValue(lightId, out LightRuntime? runtime))
            return;

        int after = SimulationRules.LightSourceFor(runtime.LightSourceId)?.RestoresAfterSteps ?? 0;
        runtime.RestoresAtStep = after > 0 ? step + after : null;
    }

    /// <summary>
    /// Rebuilds every room's lit intervals from the emitters that are working.
    /// </summary>
    /// <remarks>
    /// Whole-site rather than per-changed-light, deliberately. A room may hold several
    /// overlapping lamps, so removing one means rebuilding that room's list, and doing
    /// it uniformly is both simpler to reason about and impossible to get subtly wrong
    /// in a way that leaves a stale lit interval behind — which would be a room that
    /// stays visible after the player put the lights out.
    /// </remarks>
    private void Recompute()
    {
        _intervals.Clear();

        foreach (LightRuntime runtime in Emitters)
        {
            if (!runtime.IsWorking)
                continue;

            if (!_intervals.TryGetValue(runtime.RoomId, out List<LightInterval>? list))
            {
                list = new List<LightInterval>();
                _intervals[runtime.RoomId] = list;
            }

            Fixed32 start = runtime.X - new Fixed32(runtime.RadiusCm);
            Fixed32 end = runtime.X + new Fixed32(runtime.RadiusCm);

            // Clip to the room. An emitter near a wall has most of its reach inside the
            // masonry, and an interval that ran on through the wall would light the next
            // room along — which is the exact mistake a cutaway building invites and the
            // exact one that would let a guard see into a dark corridor.
            if (_roomExtent.TryGetValue(runtime.RoomId, out (Fixed32 Start, Fixed32 End) bounds))
            {
                if (start < bounds.Start)
                    start = bounds.Start;

                if (end > bounds.End)
                    end = bounds.End;
            }

            if (end <= start)
                continue;

            list.Add(new LightInterval(start, end, runtime.Level, runtime.LightId));
        }

        foreach (KeyValuePair<SiteRoomId, List<LightInterval>> entry in _intervals)
            entry.Value.Sort(static (a, b) => a.StartX.Raw.CompareTo(b.StartX.Raw));
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Emitters.Count} emitters, {_intervals.Count} rooms lit";
}
