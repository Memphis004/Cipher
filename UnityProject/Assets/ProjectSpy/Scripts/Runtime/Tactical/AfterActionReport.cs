using System;
using System.Collections.Generic;
using System.Text;
using ProjectSpy.Core;
using ProjectSpy.Core.Missions;
using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// Turns a finished mission's report into plain text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why text at all.</b> Players paste these into chat, and people paste them into
    /// bug reports. A debrief that can only be looked at is evidence the project cannot
    /// use and a story the player cannot tell, and the text export is what makes both
    /// possible. It is also the cheapest thing to test in the whole debrief: a pure
    /// function from a report to a string, with no scene and no canvas.
    /// </para>
    /// <para>
    /// <b>It reports, it does not judge.</b> Every number here came out of Core's
    /// <see cref="MissionReport"/>. Nothing is recomputed, ranked or scored on the way
    /// past — a debrief that calculated its own figures would eventually disagree with the
    /// simulation it is describing, and it would disagree in whichever direction flatters
    /// the run.
    /// </para>
    /// <para>
    /// <b>Absent is stated, not hidden.</b> A mission with no rolls prints
    /// <c>(not recorded)</c> rather than an empty section, because the difference between
    /// "nothing happened" and "this was not collected" is exactly the kind of thing a bug
    /// report turns on.
    /// </para>
    /// </remarks>
    public static class AfterActionReport
    {
        /// <summary>
        /// Renders the whole debrief.
        /// </summary>
        /// <param name="report">Core's report for a finished mission.</param>
        /// <param name="resolve">Resolves a localization key to text.</param>
        /// <param name="roomName">Names a room for the route line, by room id.</param>
        public static string Format(
            MissionReport report,
            Func<string, string> resolve = null,
            Func<SiteRoomId, string> roomName = null)
        {
            if (report is null)
                return string.Empty;

            resolve ??= key => key;
            roomName ??= id => id.ToString();

            var text = new StringBuilder(1024);

            AppendHeader(text, report, resolve);
            AppendSquad(text, report, resolve);
            AppendRoutes(text, report, roomName);
            AppendTimeline(text, report, resolve);
            AppendPerceptions(text, report);
            AppendNoises(text, report);
            AppendLoot(text, report);
            AppendSummary(text, report, resolve);

            return text.ToString().TrimEnd() + Environment.NewLine;
        }

        private static void AppendHeader(StringBuilder text, MissionReport report, Func<string, string> resolve)
        {
            text.AppendLine("PROJECT SPY - AFTER ACTION");
            text.AppendLine(new string('=', 40));

            text.Append("Mission ").Append(report.MissionId)
                .Append(" | site ").Append(report.SiteId)
                .Append(" | ").Append(report.Steps).AppendLine(" steps");
            text.Append("Class ").Append(Describe(report.Class))
                .Append(" | peak alarm ").Append(report.PeakBand)
                .Append(" | ended ").Append(report.EndBand)
                .AppendLine();
            text.Append("Objective ").Append(resolve(report.ObjectiveNameKey))
                .Append(" - ").Append(report.ObjectiveComplete ? "complete" : "incomplete")
                .AppendLine();

            if (report.CommandPostCompromised)
                text.AppendLine("Command post was compromised.");

            if (report.Aborted)
                text.AppendLine("Mission was aborted.");
        }

        private static void AppendSquad(StringBuilder text, MissionReport report, Func<string, string> resolve)
        {
            Section(text, "SQUAD");

            if (report.Agents.Count == 0)
            {
                text.AppendLine("  (no operatives recorded)");
                return;
            }

            foreach (AgentReport agent in report.Agents)
            {
                text.Append("  ").Append(resolve(string.IsNullOrEmpty(agent.RoleNameKey)
                        ? agent.AgentId.ToString()
                        : agent.RoleNameKey))
                    .Append(" - ").Append(agent.Condition)
                    .Append(" | hp ").Append(agent.Health)
                    .Append(" | injury +").Append(agent.InjuryAdded)
                    .Append(" | mental +").Append(agent.MentalDamage)
                    .Append(" | exp +").Append(agent.Exp);

                if (agent.GadgetsUnused > 0)
                    text.Append(" | gadgets unused ").Append(agent.GadgetsUnused);

                text.AppendLine();
            }
        }

        /// <summary>
        /// The route line, as the rooms each operative walked through in order.
        /// </summary>
        /// <remarks>
        /// Room-to-room rather than a drawn line, because the text export has no floor
        /// diagram to draw on and a list of room names is still enough for someone to
        /// reconstruct the approach from a site they know.
        /// </remarks>
        private static void AppendRoutes(
            StringBuilder text, MissionReport report, Func<SiteRoomId, string> roomName)
        {
            Section(text, "ROUTE");

            if (report.Routes.Count == 0)
            {
                text.AppendLine("  (no route recorded)");
                return;
            }

            // One line per operative, in the order they first appear in the route log, so
            // the section reads as people rather than as a flat event dump.
            var order = new List<AgentId>();
            var rooms = new Dictionary<AgentId, List<string>>();

            foreach (RouteStep step in report.Routes)
            {
                if (!rooms.TryGetValue(step.Agent, out List<string> names))
                {
                    names = new List<string>();
                    rooms[step.Agent] = names;
                    order.Add(step.Agent);
                }

                names.Add(roomName(step.RoomId));
            }

            foreach (AgentId agent in order)
            {
                text.Append("  ").Append(agent).Append(": ")
                    .AppendLine(string.Join(" > ", rooms[agent]));
            }
        }

        private static void AppendTimeline(StringBuilder text, MissionReport report, Func<string, string> resolve)
        {
            Section(text, "TIMELINE");

            if (report.Timeline.Count == 0)
            {
                text.AppendLine("  (empty)");
                return;
            }

            foreach (MissionLogEntry entry in report.Timeline)
            {
                text.Append("  step ").Append(entry.Step).Append("  ")
                    .AppendLine(resolve(entry.Key));
            }
        }

        private static void AppendPerceptions(StringBuilder text, MissionReport report)
        {
            Section(text, "PERCEPTIONS");

            if (report.Perceptions.Count == 0)
            {
                text.AppendLine("  (none)");
                return;
            }

            foreach (PerceptionRecord record in report.Perceptions)
            {
                text.Append("  step ").Append(record.Step)
                    .Append("  actor ").Append(record.ObserverId)
                    .Append(" saw ").Append(record.SubjectId)
                    .Append(" (").Append(record.Level).Append(')')
                    .AppendLine();
            }
        }

        private static void AppendNoises(StringBuilder text, MissionReport report)
        {
            Section(text, "NOISE");

            if (report.Noises.Count == 0)
            {
                text.AppendLine("  (none)");
                return;
            }

            foreach (NoiseLogEntry entry in report.Noises)
            {
                text.Append("  step ").Append(entry.Step)
                    .Append("  actor ").Append(entry.SourceActorId)
                    .Append(" made ").Append(entry.SourceKey)
                    .Append(" heard by ").Append(entry.Heard.Count)
                    .AppendLine();
            }
        }

        private static void AppendLoot(StringBuilder text, MissionReport report)
        {
            Section(text, "LOOT");

            if (report.Loot.Count == 0)
            {
                text.AppendLine("  (nothing carried out)");
                return;
            }

            foreach ((int table, int item, int count) in report.Loot)
                text.Append("  table ").Append(table).Append(" item ").Append(item)
                    .Append(" x").AppendLine(count.ToString());

            if (report.FundsPaid > 0)
                text.Append("  funds ").AppendLine(report.FundsPaid.ToString());

            if (report.IntelPaid > 0)
                text.Append("  intel ").AppendLine(report.IntelPaid.ToString());
        }

        private static void AppendSummary(StringBuilder text, MissionReport report, Func<string, string> resolve)
        {
            Section(text, "SUMMARY");

            if (report.Summary.Count == 0)
            {
                text.AppendLine("  (none)");
                return;
            }

            foreach ((string key, IReadOnlyList<int> args) in report.Summary)
                text.Append("  - ").AppendLine(resolve(key));
        }

        /// <summary>
        /// Rolls the debug collection flag into the text where it matters.
        /// </summary>
        /// <remarks>
        /// <c>IncludeRolls</c> is a flag rather than an empty list, and the two mean
        /// different things: "no dice were rolled" and "the dice were not recorded". A
        /// debrief that printed an empty rolls section for both would be lying to whoever
        /// is reading it to work out what went wrong.
        /// </remarks>
        public static string RollsNote(MissionReport report)
            => report is null || report.IncludeRolls
                ? string.Empty
                : "Rolls were not recorded for this mission (debug collection was off).";

        private static void Section(StringBuilder text, string title)
        {
            text.AppendLine();
            text.AppendLine(title);
            text.AppendLine(new string('-', title.Length));
        }

        private static string Describe(ResolveClass value) => value.ToString();
    }
}