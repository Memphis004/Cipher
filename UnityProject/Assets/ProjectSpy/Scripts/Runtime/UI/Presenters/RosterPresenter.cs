using System;
using System.Collections.Generic;
using System.Linq;
using ProjectSpy.Core;

namespace ProjectSpy.Unity.UI.Presenters
{
    /// <summary>
    /// Turns a roster into the sorted, filtered list the roster panel renders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and static. It takes an <see cref="IEnumerable{T}"/> of agents and returns
    /// agents — no GameObjects, no services, no clock. That is what makes it testable in
    /// EditMode without a scene, and it is also the reason it cannot quietly start reading
    /// live state: it has no way to.
    /// </para>
    /// <para>
    /// <b>Every value it surfaces is read from Core, never computed.</b> Stamina,
    /// loyalty band, deployability, skill average and salary all come straight off the
    /// agent or out of Core's lookups. The presenter decides order and visibility; it
    /// decides nothing about the numbers themselves.
    /// </para>
    /// </remarks>
    public static class RosterPresenter
    {
        /// <summary>
        /// Filters then sorts, in that order.
        /// </summary>
        /// <remarks>
        /// Filter first so the sort never sees an agent it is about to drop. The
        /// difference only shows up on large rosters, but it shows up as a wrong
        /// tie-break, which is exactly the sort of invisible bug that is hard to report
        /// later.
        /// </remarks>
        /// <param name="roster">Candidates. Null is treated as empty.</param>
        /// <param name="filter">Which agents to keep.</param>
        /// <param name="sort">Column and direction.</param>
        public static IReadOnlyList<Agent> Present(
            IEnumerable<Agent>? roster,
            RosterFilter filter,
            RosterSortKey sort,
            SortDirection direction)
            => Apply(roster, filter, sort, direction).ToList();

        /// <summary>The same pipeline, without materialising a list.</summary>
        public static IEnumerable<Agent> Apply(
            IEnumerable<Agent>? roster,
            RosterFilter filter,
            RosterSortKey sort,
            SortDirection direction)
        {
            IEnumerable<Agent> source = roster ?? Enumerable.Empty<Agent>();
            return Order(Filter(source, filter), sort, direction);
        }

        /// <summary>
        /// Keeps the agents a filter admits.
        /// </summary>
        /// <remarks>
        /// Conditions combine as AND. A player who asks for "available" and
        /// "infiltrators" wants the intersection, and an OR would show them agents they
        /// had explicitly excluded.
        /// </remarks>
        public static IEnumerable<Agent> Filter(IEnumerable<Agent>? roster, RosterFilter filter)
        {
            if (roster is null) yield break;

            foreach (Agent agent in roster)
            {
                if (agent is null)
                    continue;

                if (Matches(agent, filter))
                    yield return agent;
            }
        }

        /// <summary>True when one agent passes one filter.</summary>
        public static bool Matches(Agent agent, RosterFilter filter)
        {
            if (agent is null) return false;

            // "Available" is Core's own word for it: an agent who refuses assignment is
            // not available, whatever the UI would like to call them.
            if (filter.AvailableOnly && !agent.IsDeployable)
                return false;

            if (filter.UnassignedOnly && agent.AssignedRoomId != 0)
                return false;

            if (filter.StatusFilter.HasValue && agent.Status != filter.StatusFilter.Value)
                return false;

            if (filter.MinimumLevel > 0 && agent.Level < filter.MinimumLevel)
                return false;

            if (!string.IsNullOrEmpty(filter.ClassIdFilter))
            {
                if (!int.TryParse(filter.ClassIdFilter, out int wanted))
                    return false;

                if (agent.ClassId != wanted)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Orders the roster.
        /// </summary>
        /// <remarks>
        /// <b>Every ordering ends in the same tie-break: by agent id.</b> Without it the
        /// order of equal rows depends on the input order, so a re-sort that changes
        /// nothing visibly still shuffles rows underneath the player's cursor — which reads
        /// as the game randomly rearranging their staff mid-click.
        /// </remarks>
        public static IEnumerable<Agent> Order(
            IEnumerable<Agent>? roster,
            RosterSortKey sort,
            SortDirection direction)
        {
            if (roster is null) yield break;

            if (direction == SortDirection.Descending)
            {
                foreach (Agent agent in OrderDescending(roster, sort))
                    yield return agent;

                yield break;
            }

            foreach (Agent agent in OrderAscending(roster, sort))
                yield return agent;
        }

        private static IEnumerable<Agent> OrderAscending(IEnumerable<Agent> roster, RosterSortKey sort)
        {
            switch (sort)
            {
                case RosterSortKey.Codename:
                    foreach (Agent a in roster
                        .OrderBy(a => a.Codename, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(a => a.Id.Value))
                    {
                        yield return a;
                    }

                    break;

                case RosterSortKey.ClassName:
                    foreach (Agent a in roster
                        .OrderBy(a => AgentClasses.Find(a.ClassId)?.NameKey ?? string.Empty, StringComparer.Ordinal)
                        .ThenBy(a => a.Id.Value))
                    {
                        yield return a;
                    }

                    break;

                default:
                    foreach (Agent a in roster
                        .OrderBy(a => KeyFor(a, sort))
                        .ThenBy(a => a.Codename, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(a => a.Id.Value))
                    {
                        yield return a;
                    }

                    break;
            }
        }

        /// <summary>
        /// Descending on the key column, ascending on everything after it.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="Enumerable.Reverse{TSource}(IEnumerable{TSource})"/>
        /// on the ascending result: that would also put the tie-breaks the wrong way round,
        /// so equal rows would appear in descending name order when the player asked for
        /// descending numbers. Rows that jump about when a column is reversed are a
        /// classic unreported bug.
        /// </remarks>
        private static IEnumerable<Agent> OrderDescending(IEnumerable<Agent> roster, RosterSortKey sort)
        {
            switch (sort)
            {
                case RosterSortKey.Codename:
                    foreach (Agent a in roster
                        .OrderByDescending(a => a.Codename, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(a => a.Id.Value))
                    {
                        yield return a;
                    }

                    break;

                case RosterSortKey.ClassName:
                    foreach (Agent a in roster
                        .OrderByDescending(a => AgentClasses.Find(a.ClassId)?.NameKey ?? string.Empty, StringComparer.Ordinal)
                        .ThenBy(a => a.Id.Value))
                    {
                        yield return a;
                    }

                    break;

                default:
                    foreach (Agent a in roster
                        .OrderByDescending(a => KeyFor(a, sort))
                        .ThenBy(a => a.Codename, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(a => a.Id.Value))
                    {
                        yield return a;
                    }

                    break;
            }
        }

        private static int KeyFor(Agent agent, RosterSortKey sort) => sort switch
        {
            RosterSortKey.Status => (int)agent.Status,
            RosterSortKey.Level => agent.Level,
            RosterSortKey.Salary => (int)agent.SalaryPerWeek,
            RosterSortKey.Loyalty => agent.Loyalty,
            RosterSortKey.PhysicalStamina => agent.PhysicalStamina,
            RosterSortKey.MentalStamina => agent.MentalStamina,
            RosterSortKey.SkillAverage => agent.Skills.Average(),
            RosterSortKey.HighestSkill => agent.Skills.Highest(),
            _ => 0,
        };

        /// <summary>
        /// One row of the roster panel.
        /// </summary>
        /// <remarks>
        /// A projection rather than a view: it holds no reference to the agent, so the
        /// panel can be scrolled without dragging the whole live world along with it, and
        /// a test can assert on a row without constructing a scene.
        /// </remarks>
        public readonly struct RosterRow
        {
            /// <summary>Builds a row from an agent.</summary>
            public RosterRow(Agent agent)
            {
                if (agent is null) throw new ArgumentNullException(nameof(agent));

                Id = agent.Id;
                Name = agent.Name;
                Codename = agent.Codename;
                ClassNameKey = AgentClasses.Find(agent.ClassId)?.NameKey ?? "class.unknown";
                StatusKey = StatusKeyFor(agent.Status);
                Level = agent.Level;
                PhysicalStamina = agent.PhysicalStamina;
                MentalStamina = agent.MentalStamina;
                LoyaltyBandKey = $"loyalty.band.{agent.LoyaltyBand.ToString().ToLowerInvariant()}";
                SalaryPerWeek = agent.SalaryPerWeek;
                Skills = agent.Skills;
                IsDeployable = agent.IsDeployable;
                IsAssigned = agent.AssignedRoomId != 0;
                IsBurntOut = agent.IsBurntOut;
            }

            /// <summary>Agent id.</summary>
            public AgentId Id { get; }

            /// <summary>Real name.</summary>
            public string Name { get; }

            /// <summary>Codename.</summary>
            public string Codename { get; }

            /// <summary>Localization key for the class.</summary>
            public string ClassNameKey { get; }

            /// <summary>Localization key for the status.</summary>
            public string StatusKey { get; }

            /// <summary>Level.</summary>
            public int Level { get; }

            /// <summary>Physical stamina, 0..100.</summary>
            public int PhysicalStamina { get; }

            /// <summary>Mental stamina, 0..100.</summary>
            public int MentalStamina { get; }

            /// <summary>Localization key for the loyalty band.</summary>
            public string LoyaltyBandKey { get; }

            /// <summary>Weekly salary.</summary>
            public long SalaryPerWeek { get; }

            /// <summary>The five skills.</summary>
            public SkillSet Skills { get; }

            /// <summary>Core's own deployability verdict.</summary>
            public bool IsDeployable { get; }

            /// <summary>Whether this agent has a room.</summary>
            public bool IsAssigned { get; }

            /// <summary>Whether this agent is burnt out.</summary>
            public bool IsBurntOut { get; }

            /// <summary>Projects a whole roster, preserving order.</summary>
            public static IReadOnlyList<RosterRow> From(IEnumerable<Agent>? roster)
            {
                var rows = new List<RosterRow>();
                if (roster is null) return rows;

                foreach (Agent agent in roster)
                {
                    if (agent is not null)
                        rows.Add(new RosterRow(agent));
                }

                return rows;
            }

            /// <summary>
            /// Localization key for a status.
            /// </summary>
            /// <remarks>
            /// A key, not a word. Core never writes English for the player (rule 4), and a
            /// status is exactly the kind of thing that needs translating.
            /// </remarks>
            public static string StatusKeyFor(AgentStatus status)
                => $"agent.status.{status.ToString().ToLowerInvariant()}";
        }
    }
}
