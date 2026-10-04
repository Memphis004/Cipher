using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Sim.Batch;
using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;

namespace ProjectSpy.Sim.Batch;

/// <summary>
/// A scratch probe: a handful of seeds on a handful of sites, printing the state the
/// sweep aggregates.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a percentage is a bad diagnostic. When a sweep says "15% of missions
/// succeed", the question is always "what were the other 85% doing", and answering it
/// from the aggregate means re-deriving the aggregate. This prints the per-run facts —
/// how many objective interactables the site grew, whether anybody reached the objective
/// room, how much work was actually done — and every one of the bugs this harness has
/// found so far was visible here and invisible in the table.
/// </para>
/// <para>
/// Not a test. It asserts nothing on purpose: a probe that failed would stop the
/// investigation at the first odd seed, which is exactly when it is least useful.
/// </para>
/// </remarks>
public static class Diag
{
    public static void Run()
    {
        foreach (int tid in new[] { 11001, 11005, 11013 })
        {
            ProjectSpy.Tables.SiteTemplate t = null!;
            foreach (ProjectSpy.Tables.SiteTemplate x in ProjectSpy.Core.SimulationRules.AllSiteTemplates())
                if (x.Id == tid) t = x;

            for (int i = 0; i < 3; i++)
            {
                ulong seed = 0x5EED_0000UL + (ulong)i * 7919UL + (ulong)tid;
                MissionFixture f = MissionFixture.Create(seed, tid, t.Tier, TableObjectiveType.StealData, i + 1, MissionFixture.RolesFor(TableObjectiveType.StealData));
                var st = f.Mission;
                var driver = new SquadPolicyDriver(SquadPolicy.Stealth);
                var runner = new ProjectSpy.Core.Tactical.TacticalMissionRunner(st, new ProjectSpy.Core.RngStreams(seed));
                int reachedObjective = -1;

                while (!st.IsOver && st.Step < 2000)
                {
                    driver.Think(st, f.Composition);
                    runner.Step();
                    if (reachedObjective < 0)
                        foreach (var a in st.Squad)
                            if (st.RoomOf(a)?.Id == st.Layout.ObjectiveRoomId) { reachedObjective = (int)st.Step; break; }
                }

                int interactables = 0;
                foreach (var it in st.Layout.Interactables) interactables++;
                int objInteract = 0;
                foreach (var it in st.Layout.Interactables)
                    if (it.Kind == ProjectSpy.Core.InteractableType.Objective || it.Kind == ProjectSpy.Core.InteractableType.Terminal) objInteract++;

                Console.WriteLine($"{tid} seed{i}: objRoom={st.Layout.ObjectiveRoomId} interactables={interactables} (obj/term={objInteract}) workId={st.ObjectiveOutcome.WorkInteractableId} reachedObj@{reachedObjective} workSteps={st.ObjectiveOutcome.WorkSteps}/{st.ObjectiveOutcome.Percent} outcome={st.Outcome} step={st.Step} illegal={driver.IllegalOrders} busy={driver.BusySkips}");
            }
        }
    }
}
