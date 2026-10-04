namespace ProjectSpy.Core.Tactical;

/// <summary>
/// How suspicious one observer is of the team, and what that suspicion is currently
/// making them do.
/// </summary>
/// <remarks>
/// <para>
/// <b>A meter, not a boolean.</b> "Did this guard notice us" is the wrong question for a
/// stealth game, because the answer that matters is how close they are to acting on it.
/// A guard who has seen a shape and is still deciding is in a different situation from
/// one who is certain, and collapsing them into a flag throws away exactly the state the
/// player is trying to manage.
/// </para>
/// <para>
/// <b>Gain and decay are per archetype.</b> <c>guard_archetype</c> gives every guard a
/// suspicion gain rate and a decay rate, and they differ by a factor of two across the
/// twelve rows — a sentry with a radio is meant to be a worse problem than a nightwatch
/// man on his own. Those numbers come from the table; the scale and the bands are
/// structural, because a 0-100 scale with four named tiers is the shape of the design
/// rather than a tuning decision.
/// </para>
/// <para>
/// <b>Decay only ever runs when there is nothing new.</b> A guard who is actively being
/// fed perception does not also lose it, which is why <see cref="Decay"/> is called from
/// the perception phase rather than from the suspicion's own update: putting the two in
/// one place would make the order between them a matter of call sequence, and the
/// suspicion a guard holds would depend on whether the caller happened to decay before
/// or after perceiving.
/// </para>
/// </remarks>
public sealed class SuspicionMeter
{
    /// <summary>The top of the scale. 100 is certain.</summary>
    public const int Max = 100;

    /// <summary>Current suspicion, 0 to <see cref="Max"/>.</summary>
    public int Value { get; private set; }

    /// <summary>True while anything is still being perceived, which suppresses decay.</summary>
    public bool FedThisStep { get; set; }

    /// <summary>
    /// Adds suspicion, clamped to the scale.
    /// </summary>
    /// <remarks>
    /// Clamps rather than wrapping. A guard who is driven past 100 by a loud door and a
    /// visible intruder should be exactly as certain as one who saw the intruder alone —
    /// wrapping to a low number would make a guard who caught the whole team somehow less
    /// suspicious of a single noise than a guard who heard one creaking board.
    /// </remarks>
    public void Add(int amount)
    {
        if (amount <= 0)
            return;

        FedThisStep = true;

        // Widened to long because `suspicion_weight` on a lethal shot is 10 and a
        // perception can be multiplied by an effective-range ratio before it arrives
        // here; the product comfortably exceeds int range for a badly-configured row
        // and should clamp rather than wrap.
        long raised = (long)Value + amount;
        Value = raised > Max ? Max : (int)raised;
    }

    /// <summary>
    /// Lowers suspicion by an archetype's decay rate for one step.
    /// </summary>
    /// <remarks>
    /// Does nothing while <see cref="FedThisStep"/> is set, which is the whole reason
    /// that flag exists: a guard staring at a confirmed intruder should not also be
    /// quietly forgetting them.
    /// </remarks>
    public void Decay(int ratePerStep)
    {
        if (FedThisStep || ratePerStep <= 0)
            return;

        int lowered = Value - ratePerStep;
        Value = lowered < 0 ? 0 : lowered;
    }

    /// <summary>Clears the per-step "something is happening" flag.</summary>
    public void BeginStep() => FedThisStep = false;

    /// <summary>Sets suspicion outright. Load and test setup only.</summary>
    public void Set(int value) => Value = value < 0 ? 0 : value > Max ? Max : value;

    /// <summary>Forgets everything. Used when a guard is taken out of the fight.</summary>
    public void Clear() => Value = 0;

    /// <summary>True at or above <see cref="SuspicionTiers.CuriousThreshold"/>.</summary>
    public bool IsAware => Value >= SuspicionTiers.CuriousThreshold;

    /// <summary>True at or above <see cref="SuspicionTiers.SearchingThreshold"/>.</summary>
    public bool IsSearching => Value >= SuspicionTiers.SearchingThreshold;

    /// <summary>True at or above <see cref="SuspicionTiers.HostileThreshold"/>.</summary>
    public bool IsHostile => Value >= SuspicionTiers.HostileThreshold;

    /// <summary>Which tier the current value falls in.</summary>
    public SuspicionTier Tier => SuspicionTiers.Of(Value);

    /// <inheritdoc/>
    public override string ToString() => $"{Value}/100 {Tier}";
}

/// <summary>
/// What an observer's suspicion currently has them doing.
/// </summary>
/// <remarks>
/// Ordered, and the thresholds are structural: they are the points at which an NPC's
/// behaviour is qualitatively different rather than merely faster. Stage 4d reads these
/// tiers to pick a <c>goap_goal</c> — patrol below curious, investigate below searching,
/// pursue above it — so the tiers are the seam between the two stages rather than a set
/// of magic numbers either stage invents for itself.
/// </remarks>
public enum SuspicionTier
{
    /// <summary>Nothing has registered. Walking a route.</summary>
    Unaware = 0,

    /// <summary>Something felt wrong. Stops, looks, goes back.</summary>
    Curious = 1,

    /// <summary>Convinced something is there. Actively searching the area.</summary>
    Searching = 2,

    /// <summary>Knows what it is. Pursuing, and telling the others.</summary>
    Hostile = 3,
}

/// <summary>The tier boundaries, in one place so no system can invent its own.</summary>
public static class SuspicionTiers
{
    /// <summary>Suspicion at which an observer starts to wonder.</summary>
    public const int CuriousThreshold = 25;

    /// <summary>Suspicion at which an observer starts looking.</summary>
    public const int SearchingThreshold = 55;

    /// <summary>Suspicion at which an observer is certain and acts on it.</summary>
    public const int HostileThreshold = 80;

    /// <summary>The tier a suspicion value falls in.</summary>
    public static SuspicionTier Of(int value) => value switch
    {
        >= HostileThreshold => SuspicionTier.Hostile,
        >= SearchingThreshold => SuspicionTier.Searching,
        >= CuriousThreshold => SuspicionTier.Curious,
        _ => SuspicionTier.Unaware,
    };
}
