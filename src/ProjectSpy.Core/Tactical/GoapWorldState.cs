using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// What one NPC believes to be true, in the form the planner searches over.
/// </summary>
/// <remarks>
/// <para>
/// <b>The single most important property of this type is that it is built from one
/// NPC's knowledge and never from ground truth.</b> An NPC that has not seen the player
/// must not plan as if it had, and the only way to guarantee that is to make the
/// ground truth physically unable to reach this object. It is assembled by
/// <see cref="GoapWorldStateBuilder"/> from <see cref="PerceptionMemory"/>, the
/// NPC's own <see cref="SuspicionMeter"/>, and observations the NPC made itself. There
/// is no constructor that takes the mission, and no accessor that reads the site.
/// </para>
/// <para>
/// That is also what makes the no-omniscience test possible: corrupt every piece of
/// ground truth in a mission and a correctly-built world state is byte-identical,
/// because nothing in the build path can see it.
/// </para>
/// <para>
/// <b>Shape.</b> Flags in a <see cref="ulong"/> bitmask, counters in an
/// <c>int[]</c>. A plan for a whole building has to open, copy and discard thousands of
/// these inside a 2 ms budget, and two words plus a small array is about as cheap as it
/// gets. It is a mutable class rather than a struct precisely so that copying it for a
/// search node is one reference copy where a struct would deep-copy the array on every
/// push — and mutable, because the search applies effects to a node's state as it
/// expands.
/// </para>
/// </remarks>
public sealed class GoapWorldState
{
    /// <summary>Boolean facts, packed into <see cref="ulong"/> words.</summary>
    public ulong[] Flags { get; }

    /// <summary>Small non-negative counters, indexed by key ordinal.</summary>
    public int[] Counters { get; }

    /// <summary>Creates an all-false, all-zero world state.</summary>
    public GoapWorldState()
    {
        Flags = new ulong[GoapKeys.FlagWordCount];
        Counters = new int[GoapKeys.CounterCount];
    }

    /// <summary>
    /// Creates a copy sharing nothing with <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// The arrays are genuinely copied rather than shared, because the search writes
    /// effects into a node's state; sharing them would let one branch's effects leak
    /// into its sibling, and the result would be a plan that depends on the order
    /// branches happened to be visited — which is the determinism bug rule 6 exists to
    /// prevent, arriving through a completely respectable-looking door.
    /// </remarks>
    /// <remarks>
    /// <b>The planner does not use this.</b> It reuses one instance per search node via
    /// <see cref="CopyFrom"/> instead, because a budgeted search opens hundreds of nodes
    /// per replan across thirty guards, and two array allocations each dominates the
    /// cost of the step. The constructor remains for tests and for callers that want to
    /// keep a snapshot.
    /// </remarks>
    public GoapWorldState(GoapWorldState source)
    {
        Flags = new ulong[source.Flags.Length];
        Counters = new int[source.Counters.Length];
        Array.Copy(source.Flags, Flags, source.Flags.Length);
        Array.Copy(source.Counters, Counters, source.Counters.Length);
    }

    /// <summary>
    /// Overwrites this state with a copy of <paramref name="source"/>, allocating
    /// nothing when the layouts match.
    /// </summary>
    /// <remarks>
    /// The hot path of the search. A node's state is overwritten from its parent and
    /// then has one action's effects applied, so the instance can be reused across every
    /// node of every plan the planner ever runs. Nothing outside the planner ever holds
    /// a reference to a node's state, which is what makes reuse safe — see
    /// <see cref="GoapPlanner"/> for why that matters.
    /// </remarks>
    public void CopyFrom(GoapWorldState source)
    {
        Array.Copy(source.Flags, Flags, source.Flags.Length);
        Array.Copy(source.Counters, Counters, source.Counters.Length);
    }

    /// <summary>Reads a flag.</summary>
    public bool Get(GoapKey key)
    {
        int slot = (int)key;
        return (Flags[slot >> 6] & (1UL << (slot & 63))) != 0;
    }

    /// <summary>Writes a flag.</summary>
    public void Set(GoapKey key, bool value)
    {
        int slot = (int)key;
        ulong mask = 1UL << (slot & 63);

        if (value)
            Flags[slot >> 6] |= mask;
        else
            Flags[slot >> 6] &= ~mask;
    }

    /// <summary>Reads a counter.</summary>
    public int GetCount(GoapKey key) => Counters[KeyHelpers.CounterIndex((int)key)];

    /// <summary>Writes a counter, clamping negatives to zero.</summary>
    /// <remarks>
    /// Clamped rather than stored raw because every counter in the table is a "steps
    /// since" or a "how many", and a negative one is a bug that would otherwise show up
    /// as a guard concluding it has been missing for -1 steps.
    /// </remarks>
    public void SetCount(GoapKey key, int value) => Counters[KeyHelpers.CounterIndex((int)key)] = Math.Max(0, value);

    /// <summary>Increments a counter by one.</summary>
    public void Bump(GoapKey key) => SetCount(key, GetCount(key) + 1);

    /// <summary>Applies one action's effects to this state.</summary>
    /// <remarks>
    /// Effects are unconditional on the flags they set and unconditional on the flags
    /// they clear. An effect written as a negation is still a <em>set</em>: the row
    /// <c>effects: "SuspiciousDoorsClosed"</c> means after closing the door this is
    /// true, and a row listing <c>!NoAlarm</c> would mean the alarm is no longer
    /// nothing. Reading the negation as "clear the key instead" would be a second,
    /// undocumented polarity rule that a designer could not see from the CSV.
    /// </remarks>
    public void Apply(GoapActionDef action)
    {
        foreach (GoapKeys.Condition effect in action.Effects)
            Set(effect.Key, !effect.Negated);
    }

    /// <summary>
    /// Whether every condition in <paramref name="conditions"/> holds.
    /// </summary>
    public bool Satisfies(IReadOnlyList<GoapKeys.Condition> conditions)
    {
        foreach (GoapKeys.Condition condition in conditions)
        {
            if (Get(condition.Key) == condition.Negated)
                return false;
        }

        return true;
    }

    /// <summary>
    /// An order-independent identity for this state, used to close the search.
    /// </summary>
    /// <remarks>
    /// A 64-bit mix rather than <see cref="object.GetHashCode"/> or a string key.
    /// <see cref="object.GetHashCode"/> is not guaranteed stable across runs or process
    /// versions, and rule 6 requires identical plans from identical inputs; a string
    /// key would allocate on every node. This is FNV-1a over both arrays, computed in a
    /// fixed order, so the same state always produces the same number.
    /// </remarks>
    /// <remarks>
    /// A collision would close one node early and could yield a different plan. That is
    /// accepted deliberately: 64 bits over the few hundred states a budgeted search
    /// opens makes collision effectively impossible, whereas storing full states to
    /// compare would cost more than the search.
    /// </remarks>
    public ulong Fingerprint()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        ulong hash = offset;

        foreach (ulong word in Flags)
        {
            hash = (hash ^ word) * prime;
        }

        foreach (int value in Counters)
        {
            hash = (hash ^ (ulong)value) * prime;
        }

        return hash;
    }

    /// <summary>
    /// Every flag currently set, in ascending key order.
    /// </summary>
    /// <remarks>
    /// For the legibility surface and for tests. Ascending key order rather than the
    /// order the bits happen to fall in a word, so the same state always reads the same
    /// way in a debug panel.
    /// </remarks>
    public IReadOnlyList<string> TrueFlags()
    {
        var names = new List<string>();

        foreach (GoapKeyDef def in GoapKeys.Definitions)
        {
            if (def.Kind == GoapKeyKind.Flag && Get(def.Key))
                names.Add(def.Name);
        }

        return names;
    }
}

/// <summary>Arithmetic shared by the world state and the planner.</summary>
internal static class KeyHelpers
{
    /// <summary>The index a counter key occupies, discounting the flag band.</summary>
    /// <remarks>
    /// Flags are numbered from zero and counters from <see cref="GoapKeys.CounterBandStart"/>
    /// so the two bands are visible in the enum itself, which means a counter's array
    /// index is its ordinal less the offset. Centralised so the subtraction exists in
    /// exactly one place — getting it wrong in two would mean one of them reading and
    /// the other writing the same key, which is a bug that reads as a guard forgetting
    /// something at random.
    /// </remarks>
    public static int CounterIndex(int slot) => slot - GoapKeys.CounterBandStart;
}