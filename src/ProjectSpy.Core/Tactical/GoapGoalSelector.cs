using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// The numbers a goal's priority is scaled by, for one NPC at one moment.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than seven arguments, because these are read together to score every
/// goal an NPC is allowed to hold, and passing them separately would let a call site
/// transpose two of them — a bug that would look like a guard with the wrong
/// priorities rather than like a mistake.
/// </para>
/// <para>
/// <b>Only this NPC's own situation, plus the public alarm.</b> There is deliberately no
/// field for "what the other guards think", which is the aggregate that would turn goal
/// selection into a channel for site-wide omniscience. A guard responds to its own
/// suspicion, not to the average suspicion of the building.
/// </para>
/// </remarks>
public readonly record struct GoapPriorityInputs
{
    /// <summary>The site alarm, 0-100. Public information.</summary>
    public int Alarm { get; init; }

    /// <summary>This NPC's own suspicion, 0-100.</summary>
    public int Curiosity { get; init; }

    /// <summary>How threatened this NPC feels, 0-100.</summary>
    public int SelfPreservation { get; init; }

    /// <summary>How steady this NPC is, 0-100.</summary>
    public int Composure { get; init; }

    /// <summary>How much it cares about a colleague being down, 0-100.</summary>
    public int Ally { get; init; }

    /// <summary>How far it is from carrying out a standing instruction, 0-100.</summary>
    public int Order { get; init; }

    /// <summary>How readily it follows a rule it has been given, 0-100.</summary>
    public int Compliance { get; init; }

    /// <summary>Every input at zero. A world where nothing is wrong.</summary>
    public static GoapPriorityInputs Nothing => default;

    /// <summary>The value a named factor reads, already in its scoring polarity.</summary>
    /// <remarks>
    /// <see cref="Composure"/> is stored here as composure remaining and returned
    /// <em>inverted</em>, so that everything downstream can treat every factor as "more
    /// means more urgent" and never has to remember which ones are backwards.
    /// </remarks>
    public int ValueOf(GoapPriorityFactor factor) => factor switch
    {
        GoapPriorityFactor.Flat => 100,
        GoapPriorityFactor.Alarm => Alarm,
        GoapPriorityFactor.Curiosity => Curiosity,
        GoapPriorityFactor.SelfPreservation => SelfPreservation,
        GoapPriorityFactor.Composure => 100 - Composure,
        GoapPriorityFactor.Ally => Ally,
        GoapPriorityFactor.Order => Order,
        GoapPriorityFactor.Compliance => Compliance,
        _ => 0,
    };
}

/// <summary>
/// Picks which goal an NPC pursues, from its world state and its own circumstances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Selection is by priority, not by first match.</b> The obvious implementation —
/// walk the goal list and take the first applicable one — is what turns a table of
/// fifteen goals into a table where only the top row is ever used. Scoring all of them
/// and taking the maximum is what lets a guard be walking its route on a quiet step and
/// be raising the alarm on the next, with no special case in the code for either.
/// </para>
/// <para>
/// <b>Already-satisfied goals are excluded</b> rather than scored. A goal whose
/// satisfaction condition already holds is not something to pursue, and scoring it
/// would let a guard whose building is peaceful pick <c>Patrol</c> because it scores
/// well and then do nothing, which is indistinguishable from the planner being broken.
/// </para>
/// <para>
/// <b>Ties break on goal id.</b> Two goals with identical priority must resolve the same
/// way every run, or replays diverge. <see cref="GoapCatalogData.Goals"/> is in
/// ascending id order, so a strict <c>&gt;</c> comparison keeps the lowest id.
/// </para>
/// </remarks>
public static class GoapGoalSelector
{
    /// <summary>
    /// Every goal this NPC could pursue, most urgent first.
    /// </summary>
    /// <param name="world">What this NPC knows.</param>
    /// <param name="tags">The NPC's archetype tags.</param>
    /// <param name="inputs">Its priority inputs.</param>
    /// <param name="catalog">The parsed goal table.</param>
    /// <remarks>
    /// <para>
    /// The whole ranked list rather than only the winner, because <em>priority alone
    /// does not mean the goal can be had</em>. A guard that finds a body is full of
    /// curiosity, and <c>goal.pursue_target</c> is weighted heavily on curiosity — but
    /// there is nobody left to chase, so no action sequence reaches it. A selector that
    /// returned only the winner would hand the planner an unreachable goal and the NPC
    /// would stand still while a corpse went unexplained.
    /// </para>
    /// <para>
    /// So the caller walks this list and takes the first goal it can actually plan
    /// for. Ordering is by priority descending, then by ascending goal id — the second
    /// clause is the determinism guarantee, since two goals can easily tie and the
    /// replay has to resolve that the same way every run.
    /// </para>
    /// <para>
    /// Goals already satisfied are excluded rather than scored: a goal whose condition
    /// holds is not something to pursue, and scoring it would let a guard in a peaceful
    /// building pick "patrol" because it scores well and then do nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GoapGoalDef> Candidates(
        GoapWorldState world,
        IReadOnlyList<string> tags,
        GoapPriorityInputs inputs,
        GoapCatalogData catalog)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (tags is null) throw new ArgumentNullException(nameof(tags));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var scored = new List<(GoapGoalDef Goal, int Priority)>();

        foreach (GoapGoalDef goal in catalog.Goals)
        {
            if (!goal.AppliesTo(tags))
                continue;

            if (world.Satisfies(goal.Satisfaction))
                continue;

            int priority = Score(goal, inputs);

            if (priority <= 0)
                continue;

            scored.Add((goal, priority));
        }

        // Descending by priority; ascending goal id breaks ties, and Goals is already in
        // ascending id order so a stable sort would do — but Sort is not documented as
        // stable, so the tie-break is written out rather than assumed.
        scored.Sort(static (a, b) =>
        {
            int byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0 ? byPriority : a.Goal.Id.CompareTo(b.Goal.Id);
        });

        var ordered = new List<GoapGoalDef>(scored.Count);

        foreach ((GoapGoalDef goal, int _) in scored)
            ordered.Add(goal);

        return ordered;
    }

    /// <summary>
    /// The highest-priority goal this NPC should pursue, or null when nothing applies.
    /// </summary>
    /// <remarks>
    /// The highest-priority candidate <em>only</em>. Callers that can plan should prefer
    /// <see cref="Candidates"/> and take the first reachable one; this overload is for
    /// the debug surface and for tests that want to ask "what would this guard most want"
    /// without committing to whether it is achievable.
    /// </remarks>
    public static GoapGoalDef? Select(
        GoapWorldState world,
        IReadOnlyList<string> tags,
        GoapPriorityInputs inputs,
        GoapCatalogData catalog)
    {
        IReadOnlyList<GoapGoalDef> ordered = Candidates(world, tags, inputs, catalog);
        return ordered.Count > 0 ? ordered[0] : null;
    }

    /// <summary>
    /// A goal's priority right now: its highest-weighted factor, or the sum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <em>maximum</em> of its terms rather than the sum. A curve like
    /// <c>alarm:85,composure:30</c> reads as "this matters when the alarm is high, and
    /// a little when they are calm" — a max captures that shape, where a sum would make
    /// a goal with three terms more urgent than a goal with one term carrying a bigger
    /// weight. A sum would also mean adding terms to a curve to express a second reason
    /// silently made the goal beat goals it should not beat.
    /// </para>
    /// <para>
    /// So the score is the largest <c>weight * factor / 100</c> across the terms, which
    /// is why a <c>flat</c> term is read as a factor of 100: a standing order of 25
    /// beats a calm curiosity of 20 and loses to an alarm of 40, which is the ordering a
    /// designer writing those weights is describing.
    /// </para>
    /// </remarks>
    public static int Score(GoapGoalDef goal, GoapPriorityInputs inputs)
    {
        int best = 0;

        foreach ((GoapPriorityFactor factor, int weight) in goal.PriorityCurve)
        {
            // Integer percent-of, through the one helper the whole simulation uses, so
            // the rounding matches every other percentage in Core (rule 6).
            int scaled = SimulationRules.PercentOf(inputs.ValueOf(factor), weight);

            if (scaled > best)
                best = scaled;
        }

        return best;
    }

    /// <summary>
    /// The score of every goal this NPC could pursue, in ascending goal id order.
    /// </summary>
    /// <remarks>
    /// For the legibility surface. A player asking "why is this guard walking over here"
    /// needs the runners-up as much as the winner — a guard that chose to investigate a
    /// noise rather than raise the alarm is behaving correctly, and without the
    /// alternatives on screen that is indistinguishable from a guard that never noticed
    /// the alarm.
    /// </remarks>
    public static IReadOnlyList<GoapGoalScore> RankAll(
        GoapWorldState world,
        IReadOnlyList<string> tags,
        GoapPriorityInputs inputs,
        GoapCatalogData catalog)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));
        if (tags is null) throw new ArgumentNullException(nameof(tags));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var scored = new List<GoapGoalScore>();

        foreach (GoapGoalDef goal in catalog.Goals)
        {
            if (!goal.AppliesTo(tags))
                continue;

            bool satisfied = world.Satisfies(goal.Satisfaction);
            scored.Add(new GoapGoalScore(goal.Id, goal.NameKey, Score(goal, inputs), satisfied));
        }

        return scored;
    }
}

/// <summary>One goal's score at one moment, for the debug surface.</summary>
/// <param name="GoalId">The <c>goap_goal</c> row id.</param>
/// <param name="NameKey">Its localization key.</param>
/// <param name="Priority">Its computed priority.</param>
/// <param name="AlreadySatisfied">
/// True when its satisfaction condition holds, which is why it was not chosen.
/// </param>
public readonly record struct GoapGoalScore(int GoalId, string NameKey, int Priority, bool AlreadySatisfied);