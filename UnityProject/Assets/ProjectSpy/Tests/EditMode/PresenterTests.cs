using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Unity.UI.Presenters;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Proves the roster panel's sort and filter behave as the panel claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief asks for "sortable and filterable list". In a management game that is not
    /// a convenience — the player reads this screen to answer "who do I send", and a sort
    /// that reshuffles equal rows or a filter that quietly ORs its conditions instead of
    /// ANDing them puts the wrong agent's name under the cursor. These tests pin both.
    /// </para>
    /// <para>
    /// Pure presenter logic, so no scene and no Core world is needed: agents are
    /// constructed directly and the presenters are static.
    /// </para>
    /// </remarks>
    public sealed class RosterPresenterTests
    {
        private static Agent Agent(int id, string codename, int classId = 1001,
            AgentStatus status = AgentStatus.Idle, int level = 1,
            int physical = 100, int mental = 100, long salary = 100,
            int loyalty = 50, int infiltration = 0, int combat = 0,
            int tech = 0, int social = 0, int nerve = 0, int room = 0)
        {
            return new Agent
            {
                Id = new AgentId(id),
                Name = $"Name{id}",
                Codename = codename,
                ClassId = classId,
                Level = level,
                Status = status,
                PhysicalStamina = physical,
                MentalStamina = mental,
                SalaryPerWeek = salary,
                Loyalty = loyalty,
                AssignedRoomId = room,
                Skills = new SkillSet(infiltration, combat, tech, social, nerve),
            };
        }

        [Test]
        public void SortAscendingOrdersByTheChosenKey()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Bravo", level: 3),
                Agent(2, "Alpha", level: 1),
                Agent(3, "Charlie", level: 2),
            };

            var sorted = RosterPresenter.Present(
                roster, RosterFilter.None, RosterSortKey.Level, SortDirection.Ascending);

            Assert.That(sorted.Select(a => a.Level), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void SortDescendingReversesTheChosenKey()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Bravo", level: 3),
                Agent(2, "Alpha", level: 1),
                Agent(3, "Charlie", level: 2),
            };

            var sorted = RosterPresenter.Present(
                roster, RosterFilter.None, RosterSortKey.Level, SortDirection.Descending);

            Assert.That(sorted.Select(a => a.Level), Is.EqualTo(new[] { 3, 2, 1 }));
        }

        /// <summary>
        /// The tie-break rule, which is the one that is easy to get wrong.
        /// </summary>
        /// <remarks>
        /// Three agents at the same level must come out in the same order regardless of the
        /// input order. A sort that leaves equal rows where the input put them makes a
        /// re-click of an unchanged column header visibly shuffle the list under the
        /// player's cursor.
        /// </remarks>
        [Test]
        public void EqualRowsBreakTiesDeterministicallyRegardlessOfInputOrder()
        {
            var a = new List<Agent>
            {
                Agent(3, "Charlie", level: 2),
                Agent(1, "Alpha", level: 2),
                Agent(2, "Bravo", level: 2),
            };
            var b = new List<Agent>
            {
                Agent(2, "Bravo", level: 2),
                Agent(3, "Charlie", level: 2),
                Agent(1, "Alpha", level: 2),
            };

            var first = RosterPresenter.Present(a, RosterFilter.None, RosterSortKey.Level, SortDirection.Ascending);
            var second = RosterPresenter.Present(b, RosterFilter.None, RosterSortKey.Level, SortDirection.Ascending);

            Assert.That(first.Select(x => x.Id.Value), Is.EqualTo(second.Select(x => x.Id.Value)));
        }

        /// <summary>
        /// Descending must not also reverse the tie-break.
        /// </summary>
        /// <remarks>
        /// This is the bug a naive <c>Reverse()</c> over the ascending result introduces:
        /// equal rows come out in descending name order, so a player clicking "level"
        /// up then down sees the same values in the same order but their equal rows swapped.
        /// </remarks>
        [Test]
        public void DescendingKeepsTheTieBreakAscending()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Alpha", level: 5),
                Agent(2, "Bravo", level: 1),
                Agent(3, "Charlie", level: 5),
            };

            var sorted = RosterPresenter.Present(
                roster, RosterFilter.None, RosterSortKey.Level, SortDirection.Descending);

            Assert.That(sorted.Select(x => x.Id.Value), Is.EqualTo(new[] { 1, 3, 2 }),
                "equal rows must stay in ascending id order, not be reversed with the key");
        }

        [Test]
        public void CodenameSortIsCaseInsensitive()
        {
            var roster = new List<Agent>
            {
                Agent(1, "zulu"),
                Agent(2, "Alpha"),
                Agent(3, "mike"),
            };

            var sorted = RosterPresenter.Present(
                roster, RosterFilter.None, RosterSortKey.Codename, SortDirection.Ascending);

            Assert.That(sorted.Select(a => a.Codename), Is.EqualTo(new[] { "Alpha", "mike", "zulu" }));
        }

        [Test]
        public void StatusFilterKeepsOnlyThatStatus()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Idle1", status: AgentStatus.Idle),
                Agent(2, "Training1", status: AgentStatus.Training),
                Agent(3, "Idle2", status: AgentStatus.Idle),
            };

            var filter = new RosterFilter(string.Empty, AgentStatus.Idle, false, false, 0);
            var kept = RosterPresenter.Present(roster, filter, RosterSortKey.Codename, SortDirection.Ascending);

            Assert.That(kept.Count, Is.EqualTo(2));
            Assert.That(kept.All(a => a.Status == AgentStatus.Idle), Is.True);
        }

        /// <summary>
        /// Filters combine as AND, which is what the panel's filter bar implies.
        /// </summary>
        [Test]
        public void MultipleFiltersCombineAsAndNotOr()
        {
            var roster = new List<Agent>
            {
                Agent(1, "InfilIdle", classId: 1001, status: AgentStatus.Idle, level: 5),
                Agent(2, "InfilTrain", classId: 1001, status: AgentStatus.Training, level: 5),
                Agent(3, "OpsIdle", classId: 1002, status: AgentStatus.Idle, level: 5),
            };

            // Class 1001 AND status Idle: exactly one agent, not the union of both sets.
            var filter = new RosterFilter("1001", AgentStatus.Idle, false, false, 0);
            var kept = RosterPresenter.Present(roster, filter, RosterSortKey.Codename, SortDirection.Ascending);

            Assert.That(kept.Count, Is.EqualTo(1));
            Assert.That(kept[0].Codename, Is.EqualTo("InfilIdle"));
        }

        [Test]
        public void MinimumLevelDropsWeakerAgents()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Junior", level: 1),
                Agent(2, "Senior", level: 7),
            };

            var filter = new RosterFilter(string.Empty, null, false, false, 5);
            var kept = RosterPresenter.Present(roster, filter, RosterSortKey.Level, SortDirection.Ascending);

            Assert.That(kept.Count, Is.EqualTo(1));
            Assert.That(kept[0].Level, Is.EqualTo(7));
        }

        /// <summary>
        /// "Available only" is Core's own deployability, not a UI guess.
        /// </summary>
        [Test]
        public void AvailableOnlyUsesCoreDeployability()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Ready"),
                Agent(2, "Down", physical: 1),
                Agent(3, "Burnt", physical: 100),
            };

            // Make the third agent burnt out, which Core also treats as not deployable.
            roster[2].IsBurntOut = true;

            var filter = new RosterFilter(string.Empty, null, true, false, 0);
            var kept = RosterPresenter.Present(roster, filter, RosterSortKey.Codename, SortDirection.Ascending);

            Assert.That(kept.Count, Is.EqualTo(1));
            Assert.That(kept[0].Codename, Is.EqualTo("Ready"));
        }

        [Test]
        public void UnassignedOnlyKeepsAgentsWithNoRoom()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Free"),
                Agent(2, "Busy", room: 4005),
            };

            var filter = new RosterFilter(string.Empty, null, false, true, 0);
            var kept = RosterPresenter.Present(roster, filter, RosterSortKey.Codename, SortDirection.Ascending);

            Assert.That(kept.Count, Is.EqualTo(1));
            Assert.That(kept[0].Codename, Is.EqualTo("Free"));
        }

        [Test]
        public void SkillAverageSortUsesCoreAverage()
        {
            var roster = new List<Agent>
            {
                Agent(1, "Jack", infiltration: 100),
                Agent(2, "Balanced", infiltration: 20, combat: 20, tech: 20, social: 20, nerve: 20),
            };

            var sorted = RosterPresenter.Present(
                roster, RosterFilter.None, RosterSortKey.SkillAverage, SortDirection.Descending);

            Assert.That(sorted[0].Codename, Is.EqualTo("Balanced"),
                "20 across five skills averages 20; one skill at 100 averages 20 too, so the tie-break decides");
        }

        [Test]
        public void EmptyFilterKeepsEveryone()
        {
            var roster = new List<Agent> { Agent(1, "A"), Agent(2, "B") };

            Assert.That(RosterFilter.None.IsEmpty, Is.True);

            var kept = RosterPresenter.Present(roster, RosterFilter.None, RosterSortKey.Codename, SortDirection.Ascending);
            Assert.That(kept.Count, Is.EqualTo(2));
        }

        [Test]
        public void NullRosterIsTreatedAsEmptyRatherThanThrowing()
        {
            var kept = RosterPresenter.Present(null, RosterFilter.None, RosterSortKey.Level, SortDirection.Ascending);

            Assert.That(kept, Is.Empty);
        }

        [Test]
        public void RowProjectionCarriesLocalizationKeysNotEnglish()
        {
            var row = new RosterPresenter.RosterRow(Agent(1, "Alpha", status: AgentStatus.Training));

            // Rule 4: Core never writes English for the player, so a status must arrive as
            // a key. A test that only checked the value would let English through.
            Assert.That(row.StatusKey, Is.EqualTo("agent.status.training"));
            Assert.That(row.StatusKey, Does.StartWith("agent.status."));
        }
    }
}
