using ProjectSpy.Core.Missions;

// SimulationRules aliases the generated beans so the two same-named "room template"
// types can never be confused. This file only touches capture_site, so it repeats the
// alias locally rather than reaching into a type SimulationRules does not expose.
using CaptureSiteRow = ProjectSpy.Tables.CaptureSite;

namespace ProjectSpy.Core;

/// <summary>
/// Turns a real building into what the player has been told about it.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary where truth becomes belief, and it is the only place in the
/// codebase that deliberately misreports something. Everything downstream — the map
/// screen, the pathfinder, the rescue planner — reads a snapshot and never the layout,
/// so the lies here are the lies the player lives with.
/// </para>
/// <para>
/// <b>Filtering comes first, poisoning second.</b> A fact that the band has not revealed
/// is not in the snapshot to be falsified; the poison only rewrites what the player was
/// shown anyway. Reversing the order would let a lie appear at a band that should not
/// reveal the category at all, and the no-leak test would have to distinguish "withheld"
/// from "withheld and wrong".
/// </para>
/// <para>
/// <b>The stream is keyed by site, not by tick.</b> A sleeper who has been embedded for a
/// week still tells the same story every time you ask, so which facts are stale and
/// which doors they lie about are fixed for a given site and only the timestamp moves.
/// Keying on the tick instead would make the report flicker between truths, and a player
/// watching a door change its story would correctly conclude the map was broken rather
/// than that their source was unreliable.
/// </para>
/// </remarks>
public static class IntelSnapshotBuilder
{
    /// <summary>
    /// Namespaces this stream away from room contents and the site generator's own draws.
    /// </summary>
    /// <remarks>
    /// The tag is unique, so introducing intel does not shift a single room's furniture
    /// and a save written before this stage still regenerates the same building.
    /// </remarks>
    private const int IntelStreamTag = 0x11E1;

    /// <summary>Guard counts are unknown below the details band.</summary>
    private const int UnknownCount = -1;

    /// <summary>
    /// Builds what the player believes about a site.
    /// </summary>
    /// <param name="layout">The real building.</param>
    /// <param name="operation">The operation producing the report.</param>
    /// <param name="now">The tick the report is stamped with.</param>
    /// <param name="captures">Prisoners currently held, so a full report can place them.</param>
    public static IntelSnapshot Build(
        SiteLayout layout,
        SleeperOperation operation,
        Tick now,
        IReadOnlyList<CaptureRecord>? captures = null)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));
        if (operation is null) throw new ArgumentNullException(nameof(operation));

        int percent = Math.Clamp(operation.IntelPercent, 0, 100);
        IntelBand band = IntelBands.For(percent);
        IntelBandContents contents = IntelBands.ContentsOf(band);
        bool poisoned = operation.Status == SleeperStatus.Poisoned;

        IRng rng = StreamFor(layout);

        bool stale = contents.MayBeStale
                     && operation.TicksRunning(now)
                        > SimulationRules.Intel("intel_stale_after_ticks", 96);

        var plan = new PoisonPlan(layout, rng, poisoned);

        // Settled first: the objective claim decides the protected route, the protected
        // route decides which doors may be lied about, and the entry passes below read
        // all three. Settling afterwards produced reports that lied about the objective
        // and nothing else.
        plan.Settle(layout);

        var entries = new List<IntelEntry>();
        var rooms = BuildRoomEntries(layout, plan, contents, band, stale, rng, entries);
        BuildConnectionEntries(layout, plan, contents, stale, rng, entries);

        if (contents.RevealsPatrolRoutes)
            BuildPatrolEntries(layout, plan, contents, stale, rng, entries);

        var holdings = BuildHoldingEntries(layout, operation, captures, rng);

        entries.AddRange(holdings);

        return new IntelSnapshot
        {
            SiteId = layout.SiteTemplateId,
            TakenOnTick = now,
            IntelPercent = percent,
            Band = band,
            MapSeed = layout.MapSeed,
            IsPoisoned = poisoned,
            ClaimedEntrance = rooms.Entrance,

            // Gated here as well as inside the room pass. The plan always knows which
            // room it is going to claim — it has to, the choice is made once — and
            // reading it straight off the plan published the objective at every band
            // regardless of what the filter had already withheld.
            ClaimedObjective = contents.RevealsObjective ? plan.ClaimedObjective : null,
            ClaimedExtraction = rooms.Extraction,
            Entries = Sort(entries),
        };
    }

    /// <summary>
    /// The deterministic draw for this site's report.
    /// </summary>
    /// <remarks>
    /// Derived from the map seed rather than the world seed, because what a sleeper says
    /// about a building is a property of the building. Two sites generated from
    /// different missions of the same world must not produce the same report.
    /// </remarks>
    private static IRng StreamFor(SiteLayout layout)
        => new XorShift128Rng(
            RngStreams.DeriveSeed(
                RngStreams.DeriveSeed(layout.MapSeed, layout.SiteTemplateId),
                IntelStreamTag));

    // ---- rooms ---------------------------------------------------------------

    private static RoomFacts BuildRoomEntries(
        SiteLayout layout,
        PoisonPlan plan,
        IntelBandContents contents,
        IntelBand band,
        bool stale,
        IRng rng,
        List<IntelEntry> entries)
    {
        var facts = new RoomFacts { Entrance = contents.RevealsEntrance ? layout.EntranceRoomId : null };

        // Below the layout band the player knows one room exists. Naming the others
        // would hand over the building's shape for free, and the band exists precisely
        // to make that cost ticks.
        bool everyRoom = contents.RevealsRoomPlacement;

        foreach (SiteRoom room in layout.Rooms)
        {
            bool isEntrance = room.Id == layout.EntranceRoomId;

            if (!everyRoom && !isEntrance)
                continue;

            var roles = IntelKnownRole.None;

            if (isEntrance)
                roles |= IntelKnownRole.Entrance;

            if (contents.RevealsObjective)
            {
                SiteRoomId claimed = plan.PeekObjective();
                if (room.Id == claimed)
                    roles |= IntelKnownRole.Objective;

                facts.Objective = room.Id == claimed ? room.Id : facts.Objective;
            }

            if (contents.RevealsExtraction && layout.ExtractionRoomIds.Contains(room.Id))
            {
                roles |= IntelKnownRole.Extraction;
                facts.Extraction.Add(room.Id);
            }

            // Sampled once. Confidence and Source are two readings of one decision, and
            // drawing twice would consume two rolls and leave the stream one step further
            // on than the source suggests — a subtle way for a report to stop being
            // reproducible from its seed.
            IntelConfidence Confidence = ConfidenceFor(stale, rng);

            entries.Add(new IntelRoomEntry
            {
                RoomId = room.Id,
                FloorIndex = room.FloorIndex,
                StartX = room.StartX,
                EndX = room.EndX,
                NameKey = contents.RevealsRoomTypes ? room.NameKey : string.Empty,
                RoomTemplateId = contents.RevealsRoomTypes ? room.RoomTemplateId : 0,
                GuardCount = contents.RevealsGuardCounts
                    ? CountGuards(layout, room.Id)
                    : UnknownCount,
                CivilianCount = contents.RevealsGuardCounts
                    ? CountCivilians(layout, room.Id)
                    : UnknownCount,
                LightLevel = LightLevelOf(layout, room.Id),
                LightLevelKnown = contents.RevealsLightLevels,
                Roles = roles,
                Confidence = Confidence,
                Source = SourceFor(Confidence),
            });
        }

        return facts;
    }

    private static int CountGuards(SiteLayout layout, SiteRoomId roomId)
    {
        int count = 0;

        foreach (SiteGuard guard in layout.Guards)
        {
            if (guard.HomeRoomId == roomId)
                count++;
        }

        return count;
    }

    private static int CountCivilians(SiteLayout layout, SiteRoomId roomId)
    {
        int count = 0;

        foreach (SiteCivilian civilian in layout.Civilians)
        {
            if (civilian.RoomId == roomId)
                count++;
        }

        return count;
    }

    /// <summary>
    /// How lit a room is, taken from its emitters.
    /// </summary>
    /// <remarks>
    /// A room's reported light is the brightest emitter in it, because that is the number
    /// a player reasons about: they want to know whether a room is safe to enter, and
    /// "there is one lamp at the far end" and "there are six lamps" lead to the same
    /// decision. Taken at face value from the layout even though the entry only carries
    /// it at the details band — the gate is on whether the entry exposes it, not on
    /// whether the builder looked.
    /// </remarks>
    private static SiteLightLevel LightLevelOf(SiteLayout layout, SiteRoomId roomId)
    {
        var brightest = SiteLightLevel.Dark;

        foreach (SiteLight light in layout.Lights)
        {
            if (light.RoomId != roomId)
                continue;

            if (light.Level > brightest)
                brightest = light.Level;
        }

        return brightest;
    }

    // ---- connections ---------------------------------------------------------

    private static void BuildConnectionEntries(
        SiteLayout layout,
        PoisonPlan plan,
        IntelBandContents contents,
        bool stale,
        IRng rng,
        List<IntelEntry> entries)
    {
        if (!contents.RevealsConnections)
            return;

        foreach (SiteConnection connection in layout.Connections)
        {
            bool locked = connection.IsLocked;

            if (plan.LiesAboutLock(connection.Id))
                locked = !locked;

            IntelConfidence Confidence = ConfidenceFor(stale, rng);

            entries.Add(new IntelConnectionEntry
            {
                ConnectionId = connection.Id,
                RoomA = connection.RoomA,
                FloorIndexA = connection.FloorIndexA,
                RoomB = connection.RoomB,
                FloorIndexB = connection.FloorIndexB,
                Kind = connection.Kind,
                IsVertical = connection.IsVertical,
                IsLocked = locked,
                Confidence = Confidence,
                Source = SourceFor(Confidence),
            });
        }
    }

    // ---- patrols -------------------------------------------------------------

    private static void BuildPatrolEntries(
        SiteLayout layout,
        PoisonPlan plan,
        IntelBandContents contents,
        bool stale,
        IRng rng,
        List<IntelEntry> entries)
    {
        foreach (SiteGuard guard in layout.Guards)
        {
            IReadOnlyList<SiteRoomId> route = guard.PatrolRoute;

            if (plan.LiesAboutPatrol(guard.Id, out IReadOnlyList<SiteRoomId>? invented))
                route = invented!;

            IntelConfidence Confidence = ConfidenceFor(stale, rng);

            entries.Add(new IntelPatrolEntry
            {
                GuardId = guard.Id,
                ArchetypeId = guard.ArchetypeId,
                Role = guard.Role,
                Route = route,
                HomeRoomId = guard.HomeRoomId,
                Confidence = Confidence,
                Source = SourceFor(Confidence),
            });
        }
    }

    // ---- prisoners -----------------------------------------------------------

    private static List<IntelHoldingEntry> BuildHoldingEntries(
        SiteLayout layout,
        SleeperOperation operation,
        IReadOnlyList<CaptureRecord>? captures,
        IRng rng)
    {
        var holdings = new List<IntelHoldingEntry>();

        if (captures is null || captures.Count == 0)
            return holdings;

        // The reveal threshold is data, and the validator requires it to sit at or above
        // the top band. Checking it anyway means a table edit cannot quietly start
        // telling the player where their prisoner is at half intel.
        if (operation.IntelPercent < SimulationRules.Intel("intel_holding_room_percent", 100))
            return holdings;

        foreach (CaptureRecord capture in captures)
        {
            if (capture.HostSiteId != layout.SiteTemplateId || capture.IsLost)
                continue;

            SiteRoomId? room = HoldingRoomFor(layout, capture.CaptureSiteId, rng);
            if (room is null)
                continue;

            holdings.Add(new IntelHoldingEntry
            {
                AgentId = capture.AgentId,
                RoomId = room.Value,
                TicksUntilLost = capture.TicksUntilLost,
                Confidence = IntelConfidence.Reported,
                Source = IntelSource.CaptureRecord,
            });
        }

        return holdings;
    }

    /// <summary>
    /// Picks the room a prisoner is held in, from the tags the capture facility requires.
    /// </summary>
    /// <remarks>
    /// <c>capture_site.holding_room_tags</c> is what makes this a cell rather than a
    /// closet: a detention block wants a security or document room, a roadside hold will
    /// take a storage bay or a transit room. When no room in the building carries a
    /// matching tag the prisoner is placed in a room chosen from the whole building
    /// rather than not placed at all — the team knowing where they are is the reward for
    /// a hundred percent, and silently withholding it because a designer left a tag off a
    /// room template would make that reward unearnable without saying why.
    /// </remarks>
    private static SiteRoomId? HoldingRoomFor(SiteLayout layout, int captureSiteId, IRng rng)
    {
        if (layout.Rooms.Count == 0)
            return null;

        CaptureSiteRow? facility = null;

        foreach (CaptureSiteRow candidate in SimulationRules.AllCaptureSites())
        {
            if (candidate.Id == captureSiteId)
            {
                facility = candidate;
                break;
            }
        }

        var candidates = new List<SiteRoomId>();

        if (facility is not null)
        {
            IReadOnlyList<string> wanted = SimulationRules.Tags(facility.HoldingRoomTags);

            foreach (SiteRoom room in layout.Rooms)
            {
                foreach (string tag in room.Tags)
                {
                    if (wanted.Contains(tag))
                    {
                        candidates.Add(room.Id);
                        break;
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            foreach (SiteRoom room in layout.Rooms)
                candidates.Add(room.Id);
        }

        return candidates[rng.NextInt(0, candidates.Count)];
    }

    // ---- staleness -----------------------------------------------------------

    /// <summary>
    /// Decides whether one fact is reported as out of date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn per fact from the site stream rather than decided once for the whole report,
    /// because a uniformly stale map and a uniformly current one are both useless to read:
    /// the player has no way to tell which parts to trust, so they trust none of it.
    /// Scattering the stale facts is what makes the confidence label worth anything.
    /// </para>
    /// <para>
    /// Applied to every kind of entry — rooms, connections and patrols alike. The first
    /// version sampled only the rooms and let connections and patrols go stale wholesale,
    /// which made the <c>intel_stale_fact_percent</c> row mean three different things
    /// depending on which column of the report you were reading.
    /// </para>
    /// </remarks>
    private static IntelConfidence ConfidenceFor(bool stale, IRng rng)
    {
        if (!stale)
            return IntelConfidence.Reported;

        return rng.NextInt(1, 101)
               <= SimulationRules.Intel("intel_stale_fact_percent", 35)
            ? IntelConfidence.Stale
            : IntelConfidence.Reported;
    }

    /// <summary>
    /// The source that goes with a confidence, not the one that goes with the band.
    /// </summary>
    /// <remarks>
    /// These two cannot be decided separately. Reading the source off the operation's
    /// overall staleness while reading the confidence off a per-fact roll produced entries
    /// that said "told to you now" and "might be out of date" at the same time, which is
    /// not a state the UI can draw: a fact is either the sleeper's current word for it or
    /// their earlier one, and a fact flagged as possibly-old while credited to the live
    /// report is telling the player to doubt something nobody has doubts about.
    /// </remarks>
    private static IntelSource SourceFor(IntelConfidence confidence)
        => confidence == IntelConfidence.Stale ? IntelSource.SleeperPrior : IntelSource.Sleeper;

    // ---- ordering ------------------------------------------------------------

    /// <summary>
    /// Sorts entries so two snapshots of the same building compare equal.
    /// </summary>
    /// <remarks>
    /// Grouped by kind, then by the id each kind is identified by. A determinism test
    /// that compared entry lists in construction order would pass even if the builder
    /// started emitting rooms in a different order, because the layout's own room order
    /// is stable — this is the check that a future refactor of the loop order has not
    /// changed anything.
    /// </remarks>
    private static IReadOnlyList<IntelEntry> Sort(List<IntelEntry> entries)
    {
        entries.Sort((left, right) =>
        {
            int byKind = KindRank(left).CompareTo(KindRank(right));
            return byKind != 0 ? byKind : IdOf(left).CompareTo(IdOf(right));
        });

        return entries;
    }

    private static int KindRank(IntelEntry entry) => entry switch
    {
        IntelRoomEntry => 0,
        IntelConnectionEntry => 1,
        IntelPatrolEntry => 2,
        IntelHoldingEntry => 3,
        _ => 4,
    };

    private static int IdOf(IntelEntry entry) => entry switch
    {
        IntelRoomEntry room => room.RoomId.Value,
        IntelConnectionEntry connection => connection.ConnectionId.Value,
        IntelPatrolEntry patrol => patrol.GuardId.Value,
        IntelHoldingEntry holding => holding.AgentId.Value,
        _ => 0,
    };

    /// <summary>The rooms a report marked as special, carried out of the room pass.</summary>
    private sealed class RoomFacts
    {
        public SiteRoomId? Entrance { get; set; }

        public SiteRoomId? Objective { get; set; }

        public List<SiteRoomId> Extraction { get; } = new();
    }
}