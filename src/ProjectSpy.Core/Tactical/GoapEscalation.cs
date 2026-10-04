using ProjectSpy.Core.Missions;

namespace ProjectSpy.Core.Tactical;

/// <summary>
/// Why a radio-carrying guard has or has not escalated the whole site.
/// </summary>
/// <remarks>
/// Kept as its own type rather than a boolean on the actor because the brief's
/// requirement is a <em>mechanic with counter-play</em>, and a boolean cannot express
/// why. A guard who never had a radio, a guard whose radio was cut, and a guard who was
/// silenced before transmitting all leave the site un-escalated, and a player needs to be
/// able to tell those three apart to learn what worked.
/// </remarks>
public enum GoapRadioOutcome
{
    /// <summary>Carries no radio. Nothing to prevent.</summary>
    NoRadio = 0,

    /// <summary>
    /// Has a radio and has identified the team, but has not yet transmitted.
    /// </summary>
    /// <remarks>
    /// The window the brief asks for. It lasts <c>goap_radio_transmit_steps</c> steps
    /// after the sighting, and it is the whole reason taking a carrier down quickly or
    /// cutting the wire is worth anything.
    /// </remarks>
    Pending = 1,

    /// <summary>Transmitted. The whole site knows.</summary>
    Transmitted = 2,

    /// <summary>Silenced before transmitting. The site never found out from this guard.</summary>
    /// <remarks>
    /// Distinct from <see cref="NoRadio"/> deliberately: the guard saw somebody and was
    /// stopped. That is a successful silent takedown, and it is a different achievement
    /// from never having had a radio at all.
    /// </remarks>
    SilencedBeforeTransmit = 3,
}

/// <summary>
/// Radio escalation: one guard with a handset can tell the whole building.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mechanic.</b> A guard whose archetype sets <c>carries_radio</c> and who
/// positively identifies the team does not merely become suspicious — within
/// <c>goap_radio_transmit_steps</c> they transmit, and the site alarm jumps by
/// <c>goap_radio_alarm_percent</c>. That is the brief's fifth emergent behaviour, and it
/// is the reason a lone radio-carrying responder is a different problem from a lone
/// rifleman.
/// </para>
/// <para>
/// <b>The counter-play.</b> Two things prevent it, and both are checked before the timer
/// runs out: the carrier is taken down (no longer <see cref="ActorCondition.Active"/>), or
/// their radio is cut. Neither is free — the brief calls this out as a tactic, not a
/// given — but between them they are what turns "spotted" from a mission-ending event
/// into a recoverable one.
/// </para>
/// <para>
/// <b>Why it is a rule rather than a formula.</b> It sits here as its own type instead of
/// as arithmetic inside the alarm system because it is the one place where a single
/// NPC's knowledge propagates to the whole site. Making it explicit is what lets the
/// test suite prove that a silenced carrier really does prevent escalation, rather than
/// the prevention being an emergent accident of some other ordering.
/// </para>
/// </remarks>
public static class GoapRadioSystem
{
    /// <summary>How many steps a carrier has between spotting somebody and transmitting.</summary>
    private static int TransmitSteps
        => Math.Max(1, SimulationRules.Goap("goap_radio_transmit_steps", 20));

    /// <summary>How far one transmission pushes the site alarm, in points.</summary>
    private static int AlarmPercent
        => Math.Max(0, SimulationRules.Goap("goap_radio_alarm_percent", 45));

    /// <summary>
    /// Advances every radio-carrying NPC's transmit timer, raising the alarm for those
    /// who transmit.
    /// </summary>
    /// <param name="state">The mission.</param>
    /// <param name="director">The planning director, for each NPC's agent.</param>
    /// <param name="step">The step being resolved.</param>
    /// <returns>How many guards transmitted this step.</returns>
    /// <remarks>
    /// Visited in ascending actor id order so that two guards transmitting on the same
    /// step add their alarm in a fixed order. Summing is commutative, but visiting in
    /// order also means the first transmitter is the same guard every run, which matters
    /// for the debug surface.
    /// </remarks>
    public static int Advance(TacticalState state, GoapDirector director, long step)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (director is null) throw new ArgumentNullException(nameof(director));

        int transmitted = 0;

        foreach (GoapAgent agent in director.AgentsInOrder())
        {
            TacticalActor actor = agent.Actor;

            if (!agent.CarriesRadio)
                continue;

            if (OutcomeFor(agent, step) != GoapRadioOutcome.Transmitted)
                continue;

            if (agent.Findings.RadioTransmitted)
                continue;

            agent.Findings.RadioTransmitted = true;
            state.Alarm.Raise(AlarmPoints());
            state.Record("log.radio.transmitted", actor.Id.Value);
            transmitted++;
        }

        return transmitted;
    }

    /// <summary>
    /// Where this guard stands with their radio right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three-way answer, and the order of the checks is the mechanic:
    /// </para>
    /// <list type="number">
    /// <item>No radio, or the player cut it: nothing can be reported.</item>
    /// <item>The guard cannot act — taken down, or otherwise silenced: they will not
    /// transmit, however close the timer is.</item>
    /// <item>The timer has run out: the site knows.</item>
    /// </list>
    /// <para>
    /// Checked in that order deliberately. A silenced guard whose timer expired must
    /// still count as silenced, or taking somebody down after they had already spoken
    /// would read as having prevented something that had already happened.
    /// </para>
    /// </remarks>
    public static GoapRadioOutcome OutcomeFor(GoapAgent agent, long step)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        if (!agent.CarriesRadio || agent.Findings.RadioCut)
            return GoapRadioOutcome.NoRadio;

        if (agent.Findings.RadioTransmitted)
            return GoapRadioOutcome.Transmitted;

        // Silenced: they saw somebody and are not reporting it. This outranks the timer,
        // because a guard who has been taken down does not get to finish their sentence.
        if (!agent.Actor.CanAct)
            return GoapRadioOutcome.SilencedBeforeTransmit;

        // Only a guard who has positively identified the team transmits. One who heard a
        // noise is looking, not reporting, and a radio that escalated on every noise
        // would make the building announce itself for free.
        if (!agent.Findings.ContactIdentified && !agent.Actor.Memory.HasIdentification)
            return GoapRadioOutcome.NoRadio;

        return step - agent.Actor.Memory.LastSeenStep >= TransmitSteps
            ? GoapRadioOutcome.Transmitted
            : GoapRadioOutcome.Pending;
    }

    /// <summary>The alarm points one transmission is worth.</summary>
    private static int AlarmPoints()
        => SimulationRules.PercentOf(AlarmState.Max, AlarmPercent);
}

/// <summary>
/// Body discovery: whether a guard walking past a hidden body finds it.
/// </summary>
/// <remarks>
/// <para>
/// The brief's fourth behaviour is that a hidden body delays discovery and that the
/// hiding place's quality matters. Modelled as a delay rather than a probability so the
/// outcome is deterministic: a body in a good hiding place is found later, and a body in
/// a bad one is found sooner, and neither depends on a roll. That is deliberate — a
/// stealth player is reasoning about information, and a random roll on whether their
/// plan worked is the opposite of the legible system the brief asks for.
/// </para>
/// <para>
/// The delay comes from <c>goap_rule.body_hide_quality_steps</c>. A guard who walks
/// past a hidden body notices it once that many steps have elapsed since it was hidden,
/// scaled by the hiding quality: quality 100 is the full delay, quality 0 is immediate.
/// </para>
/// </remarks>
public static class GoapBodyDiscovery
{
    /// <summary>
    /// The step a guard will notice this body on, given when it was hidden and where.
    /// </summary>
    /// <param name="hiddenOnStep">When the body was put where it is.</param>
    /// <param name="hidingQuality">How good the place is, 0-100.</param>
    /// <returns>
    /// The step at which discovery happens, or <see cref="long.MaxValue"/> when the
    /// hiding place is good enough that it is never found on this patrol.
    /// </returns>
    /// <remarks>
    /// The delay scales linearly with quality, so the table value is the ceiling rather
    /// than a constant: a 100-quality hiding place delays discovery for the full window
    /// and a 50-quality one for half of it. A flat delay would make the quality column
    /// decorative.
    /// </remarks>
    public static long DiscoveryStep(long hiddenOnStep, int hidingQuality)
    {
        int full = Math.Max(0, SimulationRules.Goap("goap_body_hide_quality_steps", 40));
        int quality = Math.Clamp(hidingQuality, 0, 100);

        return hiddenOnStep + SimulationRules.PercentOf(full, quality);
    }

    /// <summary>
    /// Whether a guard has discovered this body by <paramref name="step"/>.
    /// </summary>
    public static bool IsDiscovered(long hiddenOnStep, int hidingQuality, long step)
        => step >= DiscoveryStep(hiddenOnStep, hidingQuality);

    /// <summary>
    /// How visible a body is at a given hiding quality, as a percentage.
    /// </summary>
    /// <remarks>
    /// The inverse of the delay, and what the perception path reads. Exposed here so
    /// there is one definition: a body in a cupboard is barely detectable, one in the
    /// middle of a lit corridor is entirely so.
    /// </remarks>
    public static int VisibilityPercent(int hidingQuality)
    {
        int quality = Math.Clamp(hidingQuality, 0, 100);

        return 100 - quality;
    }
}