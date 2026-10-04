using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Sim.Batch;

/// <summary>
/// What locked doors do to a site, measured before anything is changed.
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and they are not the same question. "How many sites have a locked
/// door" is a property of the generator and is already visible in
/// <c>site_gen_rule.site_locked_door_percent</c>. The question that decides whether the
/// number is a problem is <b>"can this squad get to the objective at all"</b>, which
/// depends on the graph and not on the count: one locked door on a corridor is a
/// decision, and three locked doors on a loop with one unlocked way round are free.
/// </para>
/// <para>
/// So this reports reachability, not counts. <see cref="Sites"/> is the headline: sites
/// where the objective is reachable only through a locked connection. Those are sites
/// on which the answer to "how does the squad get in" is always the same, and it is
/// never "walk".
/// </para>
/// <para>
/// Read-only. It generates sites and asks the pathfinder questions; it never runs a
/// mission, so it can be run before and after a generator change and the two numbers
/// compared.
/// </para>
/// </remarks>
public static class DoorSurvey
{
    /// <summary>What the sweep found, in one place.</summary>
    /// <param name="Sites">Sites generated.</param>
    /// <param name="ObjectiveBehindALock">
    /// Sites where every route from the entrance to the objective room crosses a locked
    /// connection.</param>
    /// <param name="ExtractionBehindALock">
    /// Sites where every route from the objective room to an extraction room crosses a
    /// locked connection.</param>
    /// <param name="SitesWithAnyLock">Sites with at least one locked connection at all.</param>
    /// <param name="TotalRooms">Rooms across every generated site.</param>
    /// <param name="RoomsOnlyReachableThroughALock">
    /// Rooms with no route from the entrance that avoids locked connections.</param>
    /// <param name="RoomsTheSquadCannotRouteTo">
    /// Rooms from which a squad member's own router finds no route to the objective,
    /// counting the options that router actually uses.</param>
    /// <param name="RoomsTheSquadCannotRouteToAnyExit">
    /// Rooms from which a squad member's own router finds no route to any exit.</param>
    public readonly record struct Result(
        int Sites,
        int ObjectiveBehindALock,
        int ExtractionBehindALock,
        int SitesWithAnyLock,
        int TotalRooms,
        int RoomsOnlyReachableThroughALock,
        int RoomsTheSquadCannotRouteTo,
        int RoomsTheSquadCannotRouteToAnyExit)
    {
        /// <summary>Fraction of sites whose objective is only reachable through a lock.</summary>
        public double ObjectiveLockedFraction =>
            Sites == 0 ? 0 : (double)ObjectiveBehindALock / Sites;

        /// <summary>Fraction of sites whose way out is only through a lock.</summary>
        public double ExtractionLockedFraction =>
            Sites == 0 ? 0 : (double)ExtractionBehindALock / Sites;

        /// <summary>Fraction of sites with at least one locked connection.</summary>
        public double AnyLockFraction =>
            Sites == 0 ? 0 : (double)SitesWithAnyLock / Sites;

        /// <summary>Fraction of rooms unreachable without opening a lock.</summary>
        public double RoomLockedFraction =>
            TotalRooms == 0 ? 0 : (double)RoomsOnlyReachableThroughALock / TotalRooms;

        /// <summary>
        /// Fraction of rooms from which the squad's own router cannot reach the objective.
        /// </summary>
        /// <remarks>
        /// A different question from <see cref="ObjectiveLockedFraction"/>, and the one
        /// that matches what a player sees. That one asks whether a lock is in the way;
        /// this one asks whether <em>the routing the squad actually uses</em> finds a way
        /// at all. A squad standing in a room with a perfectly open route to the
        /// objective is the failure this number exists to catch.
        /// </remarks>
        public double SquadUnroutableFraction =>
            TotalRooms == 0 ? 0 : (double)RoomsTheSquadCannotRouteTo / TotalRooms;

        /// <summary>Fraction of rooms from which the squad's router finds no way out.</summary>
        public double SquadCannotExitFraction =>
            TotalRooms == 0 ? 0 : (double)RoomsTheSquadCannotRouteToAnyExit / TotalRooms;
    }

    /// <summary>
    /// Generates <paramref name="sitesPerTemplate"/> sites per template and measures.
    /// </summary>
    /// <param name="sitesPerTemplate">Sites per template.</param>
    /// <param name="quiet">Suppress the per-template table when true.</param>
    public static Result Run(int sitesPerTemplate, bool quiet = false)
    {
        if (sitesPerTemplate < 1)
            throw new ArgumentOutOfRangeException(nameof(sitesPerTemplate));

        int sites = 0;
        int objectiveLocked = 0;
        int extractionLocked = 0;
        int anyLock = 0;
        int rooms = 0;
        int roomsLocked = 0;
        int unroutable = 0;
        int noExit = 0;

        if (!quiet)
        {
            Console.WriteLine(
                "template  tier  sites  objBehindLock  exitBehindLock  roomsLocked  unroutableToObj  noExit");
            Console.WriteLine(new string('-', 66));
        }

        foreach (ProjectSpy.Tables.SiteTemplate template in SimulationRules.AllSiteTemplates())
        {
            int tObjective = 0;
            int tExtraction = 0;
            int tAny = 0;
            int tRooms = 0;
            int tRoomsLocked = 0;
            int tUnroutable = 0;
            int tNoExit = 0;

            for (int index = 0; index < sitesPerTemplate; index++)
            {
                ulong seed = 0x0D00_0000UL + (ulong)index * 2654435761UL + (ulong)template.Id;
                int missionId = index + 1;

                SiteLayout layout = SiteGenerator.Generate(
                    template.Id, template.Tier, missionId, seed,
                    SiteGenerator.DeriveMapSeed(seed, missionId));

                sites++;
                tAny += HasAnyLock(layout) ? 1 : 0;

                if (NeedsALock(layout, layout.EntranceRoomId, layout.ObjectiveRoomId))
                    tObjective++;

                if (EveryExtractionNeedsALock(layout))
                    tExtraction++;

                tRooms += layout.AllRooms.Count;
                tRoomsLocked += RoomsBehindALock(layout);
                tUnroutable += RoomsTheSquadCannotRoute(layout, layout.ObjectiveRoomId);
                tNoExit += RoomsWithNoSquadRouteToAnyExit(layout);
            }

            sites += 0;
            objectiveLocked += tObjective;
            extractionLocked += tExtraction;
            anyLock += tAny;
            rooms += tRooms;
            roomsLocked += tRoomsLocked;
            unroutable += tUnroutable;
            noExit += tNoExit;

            if (!quiet)
            {
                Console.WriteLine(
                    $"{template.Id,-8}  {template.Tier,-4}  {sitesPerTemplate,-5}  "
                    + $"{tObjective,-13}  {tExtraction,-15}  {tRoomsLocked}/{tRooms,-4}  "
                    + $"{tUnroutable,-15}  {tNoExit}");
            }
        }

        return new Result(
            sites, objectiveLocked, extractionLocked, anyLock, rooms, roomsLocked, unroutable, noExit);
    }

    /// <summary>Whether this site grew any locked connection at all.</summary>
    private static bool HasAnyLock(SiteLayout layout)
    {
        foreach (SiteConnection connection in layout.Connections)
        {
            if (connection.IsLocked)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether every route between two rooms crosses a locked connection.
    /// </summary>
    /// <remarks>
    /// Asked twice with opposite <see cref="PathOptions.AllowLocked"/>, and a site
    /// counts as needing a lock only when the two disagree. A route that is impossible
    /// either way is not "behind a lock", it is a broken building, and counting it here
    /// would hide that behind a much more comfortable number.
    /// </remarks>
    private static bool NeedsALock(SiteLayout layout, SiteRoomId from, SiteRoomId to)
    {
        if (!from.IsValid || !to.IsValid || from == to)
            return false;

        bool anyRoute = Pathfinder
            .FindRoute(layout, null, from, to, new PathOptions { AllowLocked = false })
            .ReachedTarget;

        if (anyRoute)
            return false;

        return Pathfinder
            .FindRoute(layout, null, from, to, new PathOptions { AllowLocked = true })
            .ReachedTarget;
    }

    /// <summary>
    /// Whether every way out of the objective crosses a lock.
    /// </summary>
    /// <remarks>
    /// "Every" over every extraction room, because one open way out is enough and the
    /// interesting number is the site where there is none.
    /// </remarks>
    private static bool EveryExtractionNeedsALock(SiteLayout layout)
    {
        IReadOnlyList<SiteRoomId> exits = layout.ExtractionRoomIds;

        if (exits.Count == 0 || !layout.ObjectiveRoomId.IsValid)
            return false;

        foreach (SiteRoomId exit in exits)
        {
            if (!NeedsALock(layout, layout.ObjectiveRoomId, exit))
                return false;
        }

        return true;
    }

    /// <summary>
    /// How many rooms the squad's own router cannot get from to a destination.
    /// </summary>
    /// <remarks>
    /// Measured with <see cref="PathOptions.ForAgent"/>, which is what
    /// <c>RoleBehaviours</c> routes role behaviours with. Using the default options here
    /// would answer a question nobody is asking: the question is not whether the
    /// building is connected, it is whether the code that moves the squad can see the
    /// connection.
    /// </remarks>
    private static int RoomsTheSquadCannotRoute(SiteLayout layout, SiteRoomId destination)
    {
        if (!destination.IsValid)
            return 0;

        int behind = 0;

        foreach (SiteRoom room in layout.AllRooms)
        {
            if (room.Id == destination)
                continue;

            if (!Pathfinder
                    .FindRoute(layout, null, room.Id, destination, PathOptions.ForAgent)
                    .ReachedTarget)
            {
                behind++;
            }
        }

        return behind;
    }

    /// <summary>How many rooms the squad's router cannot get out of at all.</summary>
    private static int RoomsWithNoSquadRouteToAnyExit(SiteLayout layout)
    {
        int behind = 0;

        foreach (SiteRoom room in layout.AllRooms)
        {
            bool escape = false;

            foreach (SiteRoomId exit in layout.ExtractionRoomIds)
            {
                if (room.Id == exit
                    || Pathfinder
                        .FindRoute(layout, null, room.Id, exit, PathOptions.ForAgent)
                        .ReachedTarget)
                {
                    escape = true;
                    break;
                }
            }

            if (!escape)
                behind++;
        }

        return behind;
    }

    /// <summary>
    /// How many rooms on this site have no lock-free route from the entrance.
    /// </summary>
    private static int RoomsBehindALock(SiteLayout layout)
    {
        if (!layout.EntranceRoomId.IsValid)
            return 0;

        int behind = 0;

        foreach (SiteRoom room in layout.AllRooms)
        {
            if (NeedsALock(layout, layout.EntranceRoomId, room.Id))
                behind++;
        }

        return behind;
    }
}