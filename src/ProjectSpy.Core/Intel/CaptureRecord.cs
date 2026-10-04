namespace ProjectSpy.Core;

/// <summary>
/// An agency operative being held somewhere the player cannot currently reach.
/// </summary>
/// <remarks>
/// <para>
/// Stage 4b needs this because the rescue interaction depends on two things at once: a
/// room to walk into, and a clock. Neither is on <see cref="Agent"/>, because an agent
/// who is captured is still a roster member — removing them would quietly shrink the
/// agency and break every id anyone else was holding — and putting a countdown on the
/// agent would make one number mean two different things depending on status.
/// </para>
/// <para>
/// <see cref="HostSiteId"/> is the site whose <see cref="Missions.SiteLayout"/> holds
/// them. <see cref="CaptureSiteId"/> is the <c>capture_site</c> row describing the kind
/// of facility it is — its grade, how long a prisoner survives there, and the room tags
/// a holding room must have. They are separate because the facility is a property of the
/// enemy and the building is a property of the map, and the same row decides both.
/// </para>
/// </remarks>
public sealed class CaptureRecord
{
    /// <summary>The missing operative.</summary>
    public AgentId AgentId { get; init; }

    /// <summary>
    /// Foreign key into <c>capture_site</c>: what sort of place is holding them.
    /// </summary>
    public int CaptureSiteId { get; init; }

    /// <summary>
    /// The site this prisoner is held in, and whose layout their room is picked from.
    /// </summary>
    /// <remarks>
    /// This is the site the rescue mission runs against, and the one a sleeper operation
    /// has to reach full intel on to reveal the room. At any lower intel percentage the
    /// team knows the prisoner exists somewhere in the building and nothing more, which
    /// is what makes the search a mission rather than a walk to a door.
    /// </remarks>
    public int HostSiteId { get; init; }

    /// <summary>Tick they were taken.</summary>
    public Tick CapturedOnTick { get; init; }

    /// <summary>
    /// Ticks remaining before the prisoner is lost.
    /// </summary>
    /// <remarks>
    /// Set from <c>capture_site.rescue_time_limit_days</c> at capture and decremented
    /// once per strategic tick. It keeps running while a rescue mission searches the
    /// building, which is the whole reason the number is visible before the mission
    /// rather than after it.
    /// </remarks>
    public int TicksUntilLost { get; set; }

    /// <summary>True once the countdown has run out.</summary>
    public bool IsLost => TicksUntilLost <= 0;

    /// <summary>Counts the clock down one tick and reports whether the prisoner survived.</summary>
    /// <returns>True while the prisoner is still held.</returns>
    public bool AdvanceTick()
    {
        if (TicksUntilLost > 0)
            TicksUntilLost--;

        return !IsLost;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{AgentId} held at site {HostSiteId} (capture {CaptureSiteId}), {TicksUntilLost} ticks";
}