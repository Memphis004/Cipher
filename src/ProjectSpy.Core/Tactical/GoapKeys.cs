using ProjectSpy.Core.Missions;

using GoapActionRow = ProjectSpy.Tables.GoapAction;
using GoapGoalRow = ProjectSpy.Tables.GoapGoal;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Every world-state key a GOAP action or goal may name, declared once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one declaration.</b> The planner builds each NPC's
/// <see cref="GoapWorldState"/> from these keys and from nothing else, and the
/// conditions in <c>goap_action.csv</c> are written as key names. If the two could
/// drift apart the failure is silent and expensive: a precondition naming a key nobody
/// publishes can never be satisfied, so the action is one the planner pays for on
/// every replan and never executes — which looks exactly like a guard declining to
/// react, and which no other test in the suite would catch. Declaring the vocabulary
/// here and validating the tables against it at load turns that into a startup error.
/// </para>
/// <para>
/// <b>Why these are flags and small counters rather than strings.</b> A plan is
/// recomputed for a whole building inside a 2 ms step budget, and this is the state
/// that search manipulates most. Flags live in a <see cref="ulong"/> bitmask and
/// counters in a small <c>int[]</c>, so a world state is a handful of words and copying
/// one to open a search node is a memcpy rather than a dozen dictionary writes.
/// Strings would also reintroduce the hash-order iteration rule 6 forbids.
/// </para>
/// <para>
/// <b>Ordinals are append-only.</b> A save that referenced a key by ordinal would
/// break if one were renumbered, so values are never reused or reordered — the same
/// rule the table enums follow in <c>Defines/__beans__.xml</c>.
/// </para>
/// </remarks>
public enum GoapKey : int
{
    // ---- where this NPC is ---------------------------------------------------
    /// <summary>Standing at the post this archetype is assigned to.</summary>
    AtPost = 0,

    /// <summary>On the next leg of its patrol route.</summary>
    AtPatrolPoint = 1,

    /// <summary>At the power panel it is responsible for.</summary>
    AtPanel = 2,

    /// <summary>Facing the direction it was posted to watch.</summary>
    WatchingFacing = 3,

    // ---- what this npc knows about the team ----------------------------------
    /// <summary>Knows an agent is present. From sight, a body, or a report.</summary>
    HasIntruder = 4,

    /// <summary>Currently has the team in view.</summary>
    HasContact = 5,

    /// <summary>Heard a noise worth walking towards.</summary>
    HasNoiseHeard = 6,

    /// <summary>Has been shown where the noise came from and has looked.</summary>
    InspectedNoiseSource = 7,

    /// <summary>The target is visible right now.</summary>
    TargetVisible = 8,

    /// <summary>Within reach of the target.</summary>
    InMeleeRange = 9,

    /// <summary>The target is down.</summary>
    TargetDown = 10,

    /// <summary>The target has broken contact and run.</summary>
    TargetFled = 11,

    /// <summary>Reached where the team was last seen.</summary>
    ReachedLastKnownPosition = 12,

    /// <summary>The contact is cold for long enough to give up on.</summary>
    TargetLostForSteps = 13,

    // ---- this npc's own condition --------------------------------------------
    /// <summary>In cover, or behind something that stops it being shot.</summary>
    InCover = 14,

    /// <summary>Visible to whoever it is worried about.</summary>
    Exposed = 15,

    /// <summary>Under fire, or next to somebody who is.</summary>
    SelfEndangered = 16,

    /// <summary>Out of whatever was threatening it.</summary>
    OutsideDangerZone = 17,

    /// <summary>Out of ammunition.</summary>
    WeaponEmpty = 18,

    /// <summary>Has reloaded.</summary>
    Reloaded = 19,

    /// <summary>A colleague it can see is down.</summary>
    AllyDown = 20,

    /// <summary>Brought that colleague back.</summary>
    AllyRevived = 21,

    // ---- the site ------------------------------------------------------------
    /// <summary>The alarm is at zero.</summary>
    NoAlarm = 22,

    /// <summary>The alarm has been raised at this site.</summary>
    AlarmRaised = 23,

    /// <summary>The site is quiet enough to act as if nothing happened.</summary>
    SafeFromAlarm = 24,

    /// <summary>Called for backup.</summary>
    BackupRequested = 25,

    /// <summary>Something it is guarding has been interfered with.</summary>
    ObjectiveTampered = 26,

    /// <summary>That thing is secure again.</summary>
    ObjectiveSecured = 27,

    /// <summary>A prisoner is within reach.</summary>
    PrisonerNearby = 28,

    /// <summary>The prisoner is restrained.</summary>
    PrisonerRestrained = 29,

    /// <summary>The lights are out.</summary>
    PowerOut = 30,

    /// <summary>The lights are back on.</summary>
    PowerRestored = 31,

    /// <summary>A door is standing open that this post is responsible for.</summary>
    SuspiciousDoorOpen = 32,

    /// <summary>Those doors are shut.</summary>
    SuspiciousDoorsClosed = 33,

    /// <summary>A colleague is absent from a post they should be at.</summary>
    ColleagueMissing = 34,

    /// <summary>Reported that absence.</summary>
    ColleagueReportedMissing = 35,

    /// <summary>A body is visible here and has not been looked at.</summary>
    BodyUnsearched = 36,

    /// <summary>Looked at the body.</summary>
    BodySearched = 37,

    /// <summary>A room needs securing.</summary>
    RoomUnsecured = 38,

    /// <summary>That room is secure.</summary>
    RoomSecured = 39,

    // ---- civilians -----------------------------------------------------------
    /// <summary>The shift is not over.</summary>
    DayNotFinished = 40,

    /// <summary>The shift is over.</summary>
    DayFinished = 41,

    /// <summary>Work for which the civilian is responsible is done.</summary>
    WorkDone = 42,

    /// <summary>Heard the alarm go up.</summary>
    HeardAlarm = 43,

    /// <summary>Panicking.</summary>
    Panicked = 44,

    /// <summary>Got somewhere the intruder is not.</summary>
    ReachedSafePlace = 45,

    /// <summary>Hidden, and not visible from where the intruder is.</summary>
    HiddenFromIntruder = 46,

    /// <summary>Reached a guard to tell them.</summary>
    GuardReached = 47,

    // ---- integer-valued keys -------------------------------------------------
    //
    // A count rather than a flag, because "still owed something" is a fact about how
    // much is left and not about whether any of it exists. Declared as an integer so a
    // condition can say "steps since" without a second vocabulary, and so the search
    // can treat a partially-elapsed wait as distinct from an elapsed one.

    /// <summary>Steps since a noise was last heard. Zero when nothing was heard.</summary>
    StepsSinceNoise = 100,

    /// <summary>Steps since the team was last seen. Zero when nothing was.</summary>
    StepsSinceContact = 101,

    /// <summary>How many doors this NPC has found open, 0..MaxDoorsSeen.</summary>
    SuspiciousDoorsSeen = 102,

    /// <summary>How many colleagues are missing from this NPC's view of the site.</summary>
    ColleaguesMissingCount = 103,
}

/// <summary>How a key's value is stored in a <see cref="GoapWorldState"/>.</summary>
public enum GoapKeyKind
{
    /// <summary>A yes/no fact, held in the bitmask.</summary>
    Flag = 0,

    /// <summary>A small non-negative count, held in an int array.</summary>
    Counter = 1,
}

/// <summary>
/// One declared key: its name, its kind, and where its storage lives.
/// </summary>
/// <param name="Key">The enum value.</param>
/// <param name="Name">The name the CSV files use, which is also the debug label.</param>
/// <param name="Kind">Whether the key is a flag or a counter.</param>
/// <param name="Slot">Bit index within the flag mask, or index into the counter array.</param>
public readonly record struct GoapKeyDef(GoapKey Key, string Name, GoapKeyKind Kind, int Slot);

/// <summary>
/// The declared key vocabulary, and the one place a condition string is turned into
/// something the planner can evaluate.
/// </summary>
/// <remarks>
/// <para>
/// Parsing lives here rather than in the planner so that a malformed condition is a
/// single named failure at load instead of a per-plan exception, and so that the
/// validator in the test project and the planner in Core cannot disagree about what
/// <c>!HasIntruder</c> means.
/// </para>
/// <para>
/// <b>No hashing into iteration order.</b> Lookups return a slot index; nothing here
/// enumerates a dictionary to decide what to do next. Rule 6 makes hash order a
/// determinism bug, and a planner that picked actions out of a <c>Dictionary</c> would
/// be exactly that.
/// </para>
/// </remarks>
public static class GoapKeys
{
    /// <summary>
    /// Every declared key, in enum order, so that <see cref="GoapKey.Slot"/> and the
    /// flag-mask layout are derived from the enum rather than tracked by hand.
    /// </summary>
    private static readonly GoapKeyDef[] Defs = BuildDefs();

    /// <summary>How many <see cref="ulong"/> words the flag mask occupies.</summary>
    /// <remarks>
    /// Read from <see cref="Defs"/> rather than from <see cref="Definitions"/>. Static
    /// fields initialise in declaration order, and <see cref="Definitions"/> returns a
    /// field declared below this one, so going through it here would read null during
    /// type initialisation and take down every plan in the process with a
    /// <see cref="NullReferenceException"/> that names nothing useful.
    /// </remarks>
    public static int FlagWordCount { get; } = (Defs.Length + 63) / 64;

    /// <summary>How many counters a world state holds.</summary>
    public static int CounterCount { get; } = Defs.Length;

    /// <summary>Every declared key definition, in enum order.</summary>
    public static IReadOnlyList<GoapKeyDef> Definitions => Defs;

    /// <summary>
    /// Builds the definition table by reflection over <see cref="GoapKey"/>.
    /// </summary>
    /// <remarks>
    /// The enum member's own value is its <em>slot</em>, which keeps the declaration to
    /// one line per key. Slots must stay dense within each kind, and
    /// <see cref="ValidateLayout"/> proves that at load rather than letting a gap
    /// silently waste a counter array or overlap a bit.
    /// </remarks>
    private static GoapKeyDef[] BuildDefs()
    {
        var all = (GoapKey[])Enum.GetValues(typeof(GoapKey));
        Array.Sort(all, static (a, b) => ((int)a).CompareTo((int)b));

        return all
            .Select(key => new GoapKeyDef(key, key.ToString(), KindOf(key), (int)key))
            .ToArray();
    }

    /// <summary>
    /// The ordinal at which the counter band begins; flags occupy everything below it.
    /// </summary>
    /// <remarks>
    /// Public because the world state needs it to index its counter array, and public
    /// rather than duplicated so that the two cannot disagree about where the flag band
    /// ends. See <see cref="KindOf"/> for why the band is a number and not an
    /// attribute.
    /// </remarks>
    public const int CounterBandStart = 100;

    /// <summary>
    /// Whether a key is a flag or a counter, decided by which band its ordinal is in.
    /// </summary>
    /// <remarks>
    /// From the value rather than an attribute because the two bands are already
    /// visible in the numbering — flags below <see cref="CounterBandStart"/>, counters
    /// from it — and an attribute would let a key be declared in the wrong band without
    /// anything noticing until a plan came out wrong.
    /// </remarks>
    private static GoapKeyKind KindOf(GoapKey key)
        => (int)key >= CounterBandStart ? GoapKeyKind.Counter : GoapKeyKind.Flag;

    /// <summary>
    /// The condition syntax: a conjunction of terms, each a key name optionally negated
    /// with <c>!</c>, separated by a comma or by <c>&amp;&amp;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HasContact,TargetVisible</c> and <c>HasContact &amp;&amp; TargetVisible</c> mean
    /// the same thing, and both spellings are accepted because both are in the data:
    /// <c>goap_action.csv</c> separates terms with commas while <c>goap_goal.csv</c>
    /// writes <c>&amp;&amp;</c>. Requiring one spelling would have meant either editing
    /// the designer's data or making half the conditions silently unsatisfiable.
    /// </para>
    /// <para>
    /// It is a conjunction either way, not a disjunction. Actions describe what must be
    /// true to begin, and almost all of them genuinely need all of it: a guard cannot
    /// chase somebody it cannot see. A disjunction would make almost every action always
    /// applicable, which is the same silent no-op a missing key produces.
    /// </para>
    /// </remarks>
    public readonly record struct Condition(GoapKey Key, bool Negated);

    /// <summary>
    /// Parses a condition string, or returns null when it names an undeclared key.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing so that the caller can report the offending
    /// row: <c>TableValidator</c> turns null into "goap_action 12153: 'Foo' is not a
    /// declared key", which names the row a designer has to fix, where an
    /// <see cref="ArgumentException"/> would only name the string.
    /// </remarks>
    public static IReadOnlyList<Condition>? TryParse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Array.Empty<Condition>();

        var terms = new List<Condition>();

        // Split on the comma first and on "&&" second, rather than the other way round,
        // because a comma-separated term may itself contain no "&&" but a "&&"-separated
        // pair may also carry commas in the two halves. Doing both on one token keeps
        // each term to a single key name either way.
        foreach (string part in expression.Split(','))
        {
            foreach (string conjunct in part.Split(ConjunctionSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = conjunct.Trim();

                if (token.Length == 0)
                    continue;

                bool negated = token[0] == '!';

                if (negated)
                    token = token[1..].Trim();

                if (token.Length == 0 || !TryResolve(token, out GoapKey key))
                    return null;

                terms.Add(new Condition(key, negated));
            }
        }

        return terms;
    }

    /// <summary>The explicit conjunction separator the goal column uses.</summary>
    private static readonly string[] ConjunctionSeparator = { "&&" };

    /// <summary>
    /// Alternate spellings that mean a key already declared, resolved to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry, and it lives here rather than becoming two keys because the two names
    /// are the <em>same fact</em>: <c>goap_action.csv</c> tests <c>HasNoiseHeard</c>
    /// while <c>goap_goal.csv</c> tests <c>!NoiseHeard</c> for the same thing, and a
    /// guard that has heard something is a guard that has heard something. Declaring
    /// both would mean two independent bits that every writer has to keep in step — the
    /// classic way a world state ends up asserting "this NPC has heard a noise" and
    /// "this NPC has not" simultaneously.
    /// </para>
    /// <para>
    /// The alternative, editing the CSV so both columns agreed, was rejected because
    /// the goal column is a designer-facing field and renaming it is a data change that
    /// has to be coordinated; resolving it here keeps the data as authored and puts the
    /// correction in the one place that owns the vocabulary.
    /// </para>
    /// </remarks>
    private static readonly (string Alias, GoapKey Target)[] Aliases =
    {
        ("NoiseHeard", GoapKey.HasNoiseHeard),
    };

    /// <summary>
    /// Resolves a key name to its enum value, or false when the name is not declared.
    /// </summary>
    /// <remarks>
    /// A linear scan rather than a dictionary, and that is not an oversight. There are
    /// around fifty keys, and this runs once per condition at load rather than per
    /// node expanded, so a dense array scan beats a dictionary lookup plus its hashing
    /// and cannot allocate.
    /// </remarks>
    public static bool TryResolve(string name, out GoapKey key)
    {
        foreach (GoapKeyDef def in Defs)
        {
            if (string.Equals(def.Name, name, StringComparison.Ordinal))
            {
                key = def.Key;
                return true;
            }
        }

        foreach ((string alias, GoapKey target) in Aliases)
        {
            if (string.Equals(alias, name, StringComparison.Ordinal))
            {
                key = target;
                return true;
            }
        }

        key = default;
        return false;
    }

    /// <summary>
    /// Proves the two key bands are internally consistent before anything is planned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from <see cref="GoapCatalog.Load"/> rather than only from the test
    /// project, because the failure it catches — a flag whose ordinal is inside the
    /// counter band, or two keys sharing a slot — corrupts every world state in the
    /// building, and a sim that loads corrupt state is worse than one that refuses to
    /// start.
    /// </para>
    /// <para>
    /// The check is cheap and runs once per process, because the result is cached with
    /// the catalog.
    /// </para>
    /// </remarks>
    /// <returns>Null when the layout is sound, otherwise a description of the fault.</returns>
    public static string? ValidateLayout()
    {
        var seenSlots = new HashSet<int>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (GoapKeyDef def in Defs)
        {
            if (!seenSlots.Add(def.Slot))
                return $"goap key slot {def.Slot} is used twice ({def.Name})";

            if (!seenNames.Add(def.Name))
                return $"goap key name '{def.Name}' is declared twice";

            bool counter = def.Slot >= CounterBandStart;

            if (counter != (def.Kind == GoapKeyKind.Counter))
                return $"goap key '{def.Name}' sits in the wrong band for its kind";

            if (counter && def.Slot - CounterBandStart >= CounterSlotLimit)
                return $"goap key '{def.Name}' is beyond the counter band";
        }

        return null;
    }

    /// <summary>
    /// Upper bound on the counter band's size, so a mistyped ordinal cannot size an
    /// array from a hostile or corrupt table.
    /// </summary>
    private const int CounterSlotLimit = 64;

    /// <summary>
    /// Every key name any <c>goap_action</c> or <c>goap_goal</c> row names.
    /// </summary>
    /// <remarks>
    /// Used by <c>TableValidator</c> so that a new key a designer writes into a CSV is
    /// checked against the single declaration here rather than against a second list
    /// maintained beside it. Two lists drift; that is the whole reason this class
    /// exists.
    /// </remarks>
    public static bool IsDeclared(string name) => TryResolve(name, out _);
}

/// <summary>
/// A parsed <c>goap_action</c> row: its conditions resolved to declared keys.
/// </summary>
/// <remarks>
/// The CSV stores precondition and effect strings; the planner needs them as slot
/// indices. Resolving once at load rather than per plan is what keeps a replan inside
/// its budget, and resolving <em>at load</em> rather than lazily is what turns a typo
/// into a startup failure instead of an action that quietly never runs.
/// </remarks>
public sealed class GoapActionDef
{
    /// <summary>The <c>goap_action</c> row id. Also the deterministic tie-break order.</summary>
    public int Id { get; }

    /// <summary>Localization key. Core never produces prose.</summary>
    public string NameKey { get; }

    /// <summary>Everything that must hold for this action to be applicable.</summary>
    public IReadOnlyList<GoapKeys.Condition> Preconditions { get; }

    /// <summary>What becomes true once this action has run.</summary>
    public IReadOnlyList<GoapKeys.Condition> Effects { get; }

    /// <summary>Planner cost. From <c>base_cost</c>.</summary>
    public int BaseCost { get; }

    /// <summary>Steps the action takes to complete.</summary>
    public int DurationSteps { get; }

    /// <summary>
    /// How strongly this action outranks others when a plan is interrupted.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="BaseCost"/> because they answer different questions.
    /// Cost decides which plan is cheapest to reach a goal; priority decides what an NPC
    /// abandons when the world changes under it. Raising the alarm is urgent and cheap;
    /// searching a room is neither.
    /// </remarks>
    public int InterruptPriority { get; }

    /// <summary>Archetype tags that may perform this action. Empty means anyone.</summary>
    public IReadOnlyList<string> RequiredTags { get; }

    internal GoapActionDef(
        int id,
        string nameKey,
        IReadOnlyList<GoapKeys.Condition> preconditions,
        IReadOnlyList<GoapKeys.Condition> effects,
        int baseCost,
        int durationSteps,
        int interruptPriority,
        IReadOnlyList<string> requiredTags)
    {
        Id = id;
        NameKey = nameKey;
        Preconditions = preconditions;
        Effects = effects;
        BaseCost = baseCost;
        DurationSteps = durationSteps;
        InterruptPriority = interruptPriority;
        RequiredTags = requiredTags;
    }
}

/// <summary>
/// A parsed <c>goap_goal</c> row: when it is satisfied and how urgently it is wanted.
/// </summary>
public sealed class GoapGoalDef
{
    /// <summary>The <c>goap_goal</c> row id.</summary>
    public int Id { get; }

    /// <summary>Localization key for the goal's name.</summary>
    public string NameKey { get; }

    /// <summary>Conditions that mean the goal is already met.</summary>
    public IReadOnlyList<GoapKeys.Condition> Satisfaction { get; }

    /// <summary>Archetype tags this goal applies to. Empty means anyone.</summary>
    public IReadOnlyList<string> ValidArchetypes { get; }

    /// <summary>
    /// The priority curve, as factor/weight pairs in table order.
    /// </summary>
    /// <remarks>
    /// Kept as the raw list rather than a precomputed number because the inputs are
    /// per-NPC and per-step: <c>alarm:60</c> means "sixty per cent of the alarm level",
    /// so the same goal is urgent for one guard and not another. See
    /// <see cref="GoapGoalSelector"/>.
    /// </remarks>
    public IReadOnlyList<(GoapPriorityFactor Factor, int Weight)> PriorityCurve { get; }

    internal GoapGoalDef(
        int id,
        string nameKey,
        IReadOnlyList<GoapKeys.Condition> satisfaction,
        IReadOnlyList<string> validArchetypes,
        IReadOnlyList<(GoapPriorityFactor Factor, int Weight)> priorityCurve)
    {
        Id = id;
        NameKey = nameKey;
        Satisfaction = satisfaction;
        ValidArchetypes = validArchetypes;
        PriorityCurve = priorityCurve;
    }

    /// <summary>True when this goal applies to an NPC carrying one of these tags.</summary>
    /// <param name="tags">The NPC's archetype tags, in a fixed order.</param>
    public bool AppliesTo(IReadOnlyList<string> tags)
    {
        if (ValidArchetypes.Count == 0)
            return true;

        foreach (string tag in ValidArchetypes)
        {
            foreach (string held in tags)
            {
                if (string.Equals(tag, held, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}

/// <summary>
/// What a goal's priority is scaled by. The vocabulary is deliberately small.
/// </summary>
/// <remarks>
/// Each factor names a quantity an NPC can read from the world. A factor an NPC cannot
/// observe would let a guard act on a number it has no way of knowing, which is the
/// failure the stage-4d brief calls out hardest — so the set is exactly the things
/// <see cref="GoapWorldState"/> is built from.
/// </remarks>
public enum GoapPriorityFactor
{
    /// <summary>Always this weight. A standing order rather than a reaction.</summary>
    Flat = 0,

    /// <summary>The site-wide alarm level, 0-100.</summary>
    Alarm = 1,

    /// <summary>This NPC's own suspicion, 0-100.</summary>
    Curiosity = 2,

    /// <summary>How threatened this NPC feels, 0-100.</summary>
    SelfPreservation = 3,

    /// <summary>
    /// How much composure this NPC has <em>lost</em>, 0-100.
    /// </summary>
    /// <remarks>
    /// <b>The one factor whose polarity is inverted.</b> Every other factor is a
    /// trigger — more of the thing, more urgent the goal. This one is read backwards, so
    /// a <em>panicking</em> guard scores highest on the goals weighted by it.
    /// </remarks>
    /// <remarks>
    /// That direction is not a guess. The goals weighted by it are
    /// <c>raise_alarm</c>, <c>call_for_backup</c> and <c>report_missing_colleague</c>,
    /// and read upright they assert the opposite of what they mean: a guard at suspicion
    /// 0 has full composure and would score highest on raising the alarm, which is a calm
    /// and quiet building declaring an emergency over nothing at all. A guard raises the
    /// alarm <em>because</em> they are not holding it together.
    /// </remarks>
    Composure = 4,

    /// <summary>How much this NPC cares about a colleague being down, 0-100.</summary>
    Ally = 5,

    /// <summary>How far this NPC is from carrying out a standing instruction, 0-100.</summary>
    Order = 6,

    /// <summary>How willingly this NPC follows a rule it has been given, 0-100.</summary>
    Compliance = 7,
}

/// <summary>
/// The GOAP tables, parsed once and shared.
/// </summary>
/// <remarks>
/// <para>
/// A cache because parsing means resolving every condition string in every row, and
/// that happens on the replanning path of a 2 ms budget. The brief's "validated against
/// goap_action.csv at load time" is why this is eager: an undeclared key must be an
/// error the first time a mission starts, not a branch the planner takes per plan.
/// </para>
/// <para>
/// Actions are held in ascending id order, which is the order the planner expands them
/// in and therefore the deterministic tie-break. Sorting once here means no caller has
/// to remember to sort, and no hash iteration can leak into the search.
/// </para>
/// </remarks>
public static class GoapCatalog
{
    private static readonly object Gate = new();
    private static GoapCatalogData? _cached;

    /// <summary>
    /// The parsed tables, loading and validating them on first use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A row names a key that is not declared, or names no keys at all. Both are data
    /// faults that would otherwise present as an NPC quietly doing nothing.
    /// </exception>
    public static GoapCatalogData Load()
    {
        GoapCatalogData? cached = _cached;
        if (cached is not null)
            return cached;

        lock (Gate)
        {
            if (_cached is not null)
                return _cached;

            string? layoutProblem = GoapKeys.ValidateLayout();

            if (layoutProblem is not null)
                throw new InvalidOperationException($"goap key declaration is inconsistent: {layoutProblem}");

            var actions = new List<GoapActionDef>();
            var goals = new List<GoapGoalDef>();

            foreach (GoapActionRow row in SimulationRules.AllGoapActions())
            {
                IReadOnlyList<GoapKeys.Condition>? pre = GoapKeys.TryParse(row.Preconditions);
                IReadOnlyList<GoapKeys.Condition>? eff = GoapKeys.TryParse(row.Effects);

                if (pre is null || eff is null)
                    throw new InvalidOperationException(
                        $"goap_action {row.Id} ({row.NameKey}) names a world-state key that is not declared; " +
                        "every key in preconditions and effects must exist in GoapKey.");

                if (pre.Count == 0)
                    throw new InvalidOperationException(
                        $"goap_action {row.Id} ({row.NameKey}) has no preconditions, so it is always applicable.");

                actions.Add(new GoapActionDef(
                    row.Id,
                    row.NameKey,
                    pre,
                    eff,
                    row.BaseCost,
                    row.DurationSteps,
                    row.InterruptPriority,
                    SplitList(row.RequiredArchetypeTags)));
            }

            foreach (GoapGoalRow row in SimulationRules.AllGoapGoals())
            {
                IReadOnlyList<GoapKeys.Condition>? sat = GoapKeys.TryParse(row.SatisfactionCondition);

                if (sat is null)
                    throw new InvalidOperationException(
                        $"goap_goal {row.Id} ({row.NameKey}) names a satisfaction key that is not declared.");

                if (sat.Count == 0)
                    throw new InvalidOperationException(
                        $"goap_goal {row.Id} ({row.NameKey}) has no satisfaction condition, so it can never complete.");

                goals.Add(new GoapGoalDef(
                    row.Id,
                    row.NameKey,
                    sat,
                    SplitList(row.ValidArchetypes),
                    ParseCurve(row.Id, row.PriorityCurve)));
            }

            // Ascending id, once. Every later expansion order derives from this.
            actions.Sort(static (a, b) => a.Id.CompareTo(b.Id));
            goals.Sort(static (a, b) => a.Id.CompareTo(b.Id));

            _cached = new GoapCatalogData(actions, goals);
            return _cached;
        }
    }

    /// <summary>Forgets the cache. Tests only; a developer editing a CSV mid-session.</summary>
    internal static void ResetCache() => _cached = null;

    /// <summary>
    /// Parses <c>factor:weight</c> pairs, rejecting anything unrecognised.
    /// </summary>
    /// <remarks>
    /// A misspelled factor name throws rather than defaulting to zero. A silent zero
    /// would leave the goal selectable but never competitive, which is a guard that
    /// looks like it has decided not to care.
    /// </remarks>
    private static IReadOnlyList<(GoapPriorityFactor Factor, int Weight)> ParseCurve(int goalId, string curve)
    {
        var terms = new List<(GoapPriorityFactor, int)>();

        foreach (string part in SplitList(curve))
        {
            int colon = part.IndexOf(':');

            if (colon <= 0)
                throw new InvalidOperationException($"goap_goal {goalId}: priority term '{part}' is not factor:weight");

            string factorName = part[..colon].Trim();
            string weightText = part[(colon + 1)..].Trim();

            if (!TryParseFactor(factorName, out GoapPriorityFactor factor))
            {
                throw new InvalidOperationException(
                    $"goap_goal {goalId}: unknown priority factor '{factorName}'");
            }

            if (!int.TryParse(weightText, out int weight))
                throw new InvalidOperationException($"goap_goal {goalId}: '{part}' has a non-numeric weight");

            if (weight < 0)
                throw new InvalidOperationException($"goap_goal {goalId}: '{part}' has a negative weight");

            terms.Add((factor, weight));
        }

        if (terms.Count == 0)
            throw new InvalidOperationException($"goap_goal {goalId}: priority curve is empty, so it can never be chosen");

        return terms;
    }

    /// <summary>Resolves a factor name from the CSV.</summary>
    private static bool TryParseFactor(string name, out GoapPriorityFactor factor)
    {
        // Lower-cased because the CSV writes `alarm` and `Alarm` interchangeably by
        // habit; case carries no meaning in a data file a human types.
        switch (name.ToLowerInvariant())
        {
            case "flat": factor = GoapPriorityFactor.Flat; return true;
            case "alarm": factor = GoapPriorityFactor.Alarm; return true;
            case "curiosity": factor = GoapPriorityFactor.Curiosity; return true;
            case "self": factor = GoapPriorityFactor.SelfPreservation; return true;
            case "composure": factor = GoapPriorityFactor.Composure; return true;
            case "ally": factor = GoapPriorityFactor.Ally; return true;
            case "order": factor = GoapPriorityFactor.Order; return true;
            case "compliance": factor = GoapPriorityFactor.Compliance; return true;
            default: factor = GoapPriorityFactor.Flat; return false;
        }
    }

    /// <summary>Splits a comma-separated cell, dropping empties.</summary>
    private static IReadOnlyList<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        string[] parts = value.Split(',');
        var list = new List<string>(parts.Length);

        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
                list.Add(trimmed);
        }

        return list;
    }
}

/// <summary>The parsed GOAP tables, ready to plan against.</summary>
public sealed class GoapCatalogData
{
    /// <summary>Every action, in ascending id order. The expansion order is this order.</summary>
    public IReadOnlyList<GoapActionDef> Actions { get; }

    /// <summary>Every goal, in ascending id order.</summary>
    public IReadOnlyList<GoapGoalDef> Goals { get; }

    private readonly GoapActionDef?[] _actionsById;

    internal GoapCatalogData(IReadOnlyList<GoapActionDef> actions, IReadOnlyList<GoapGoalDef> goals)
    {
        Actions = actions;
        Goals = goals;

        int maxId = 0;
        foreach (GoapActionDef action in actions)
            maxId = Math.Max(maxId, action.Id);

        _actionsById = new GoapActionDef?[maxId + 1];

        foreach (GoapActionDef action in actions)
            _actionsById[action.Id] = action;
    }

    /// <summary>An action by id, or null. Array-indexed, so it allocates nothing.</summary>
    public GoapActionDef? Action(int id)
        => id >= 0 && id < _actionsById.Length ? _actionsById[id] : null;
}