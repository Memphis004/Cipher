using System;
using System.Collections.Generic;
using ProjectSpy.Core;
using ProjectSpy.Unity.Localisation;
using R3;
using UnityEngine;

namespace ProjectSpy.Unity.UI
{
    /// <summary>
    /// Shows the full arithmetic behind a number, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The brief's rule is that hiding the maths is a design bug: "Infiltration 62 = base
    /// 50 +8 training +10 Ghost -6 fatigue". This service exists so that rule can be
    /// honoured honestly rather than literally.
    /// </para>
    /// <para>
    /// <b>It shows the terms Core publishes, and does not invent the rest.</b> Core's
    /// <see cref="SkillBreakdown"/> currently reports two terms and explicitly names two
    /// modifiers it does <em>not</em> apply (trait bonuses and fatigue). Showing a
    /// "+10 Ghost" the simulation never used would teach the player a model of the game
    /// that does not exist, and they would then mispredict every outcome that depended on
    /// it. So the missing terms appear as explicit zeroes: "trait bonus +0" is information,
    /// and its absence would read as an incomplete tooltip.
    /// </para>
    /// <para>
    /// <b>No arithmetic happens here.</b> The service reads Core's terms and resolves their
    /// localization keys. If Core's breakdown ever changes, this follows; it never
    /// recomputes a total to fill a gap.
    /// </para>
    /// </remarks>
    public sealed class TooltipService : Services.IProjectSpyService
    {
        private readonly LocalizationService _localization;

        /// <summary>Creates the service.</summary>
        public TooltipService(LocalizationService localization)
        {
            _localization = localization;
        }

        /// <summary>Raised when a tooltip should be shown. Carries the resolved body.</summary>
        public Subject<TooltipContent> Requested { get; } = new();

        /// <summary>Raised when the visible tooltip should be dismissed.</summary>
        public Subject<Unit> Dismissed { get; } = new();

        /// <summary>Transform tooltips are parented to. Injected by the UIRoot at boot.</summary>
        public Transform TooltipLayer { get; set; }

        /// <summary>
        /// The full breakdown for one of an agent's skills.
        /// </summary>
        /// <param name="agent">The agent. Never null.</param>
        /// <param name="skill">Which skill.</param>
        /// <remarks>
        /// Reads Core's breakdown wholesale. The header carries the skill's own total and
        /// the body carries Core's terms, each resolved through localization with the
        /// delta already signed, so no view has to know how to add anything up.
        /// </remarks>
        public TooltipContent SkillBreakdown(Agent agent, SkillKind skill)
        {
            if (agent is null) throw new ArgumentNullException(nameof(agent));

            SkillBreakdown breakdown = Core.SkillBreakdown.For(agent, skill);
            return Build(breakdown);
        }

        /// <summary>The full breakdown for all five skills, as the agent window shows it.</summary>
        public IReadOnlyList<TooltipContent> AllSkills(Agent agent)
        {
            var contents = new List<TooltipContent>(SkillSet.Kinds.Length);
            foreach (SkillKind kind in SkillSet.Kinds)
                contents.Add(SkillBreakdown(agent, kind));

            return contents;
        }

        private TooltipContent Build(Core.SkillBreakdown breakdown)
        {
            var lines = new List<TooltipLine>(breakdown.Terms.Count + breakdown.UnmodelledReasons.Count);

            foreach (SkillTerm term in breakdown.Terms)
                lines.Add(Line(term.TermKey, term.Delta, isUnmodelled: false));

            foreach (SkillTerm term in breakdown.UnmodelledReasons)
                lines.Add(Line(term.TermKey, term.Delta, isUnmodelled: true));

            return new TooltipContent(
                heading: Text(breakdown.SkillKey, breakdown.Total.ToString()),
                total: breakdown.Total,
                lines: lines);
        }

        private TooltipLine Line(string key, int delta, bool isUnmodelled)
        {
            string label = Text(key, isUnmodelled ? "0" : delta.ToString());
            return new TooltipLine(label, delta, isUnmodelled);
        }

        /// <summary>
        /// Resolves a key, falling back to the key itself.
        /// </summary>
        /// <remarks>
        /// The brief requires that a missing key renders as the key with a console warning
        /// and never an exception. <see cref="LocalizationService.Get"/> already does that,
        /// so this is a pass-through kept narrow so the fallback is applied consistently
        /// even if a caller arrives here directly.
        /// </remarks>
        private string Text(string key, string argument)
            => _localization is null ? key : _localization.Format(key, argument);
    }

    /// <summary>One line of a tooltip.</summary>
    /// <param name="Label">Resolved text for the cause.</param>
    /// <param name="Delta">Signed contribution.</param>
    /// <param name="IsUnmodelled">
    /// True for a modifier Core explicitly does not apply. Shown at zero and marked, so the
    /// player learns the mechanic is absent rather than guessing from a short list.
    /// </param>
    public readonly struct TooltipLine
    {
        /// <summary>Creates a line.</summary>
        public TooltipLine(string label, int delta, bool isUnmodelled)
        {
            Label = label;
            Delta = delta;
            IsUnmodelled = isUnmodelled;
        }

        /// <summary>Resolved text naming the cause.</summary>
        public string Label { get; }

        /// <summary>Signed contribution.</summary>
        public int Delta { get; }

        /// <summary>True when Core does not apply this modifier at all.</summary>
        public bool IsUnmodelled { get; }
    }

    /// <summary>A tooltip's resolved content.</summary>
    /// <param name="Heading">Resolved title, carrying the total.</param>
    /// <param name="Total">The number the tooltip is explaining.</param>
    /// <param name="Lines">The arithmetic, one line per term.</param>
    public readonly struct TooltipContent
    {
        /// <summary>Creates content.</summary>
        public TooltipContent(string heading, int total, IReadOnlyList<TooltipLine> lines)
        {
            Heading = heading;
            Total = total;
            Lines = lines ?? Array.Empty<TooltipLine>();
        }

        /// <summary>Resolved title.</summary>
        public string Heading { get; }

        /// <summary>The number being explained.</summary>
        public int Total { get; }

        /// <summary>The arithmetic behind it.</summary>
        public IReadOnlyList<TooltipLine> Lines { get; }

        /// <summary>
        /// The terms that actually moved the number.
        /// </summary>
        /// <remarks>
        /// Split out because a view that wants a compact tooltip ("62, from training")
        /// should not have to filter out the zeroes itself and get it subtly wrong.
        /// </remarks>
        public IReadOnlyList<TooltipLine> Meaningful
        {
            get
            {
                var kept = new List<TooltipLine>();
                foreach (TooltipLine line in Lines)
                {
                    if (!line.IsUnmodelled && line.Delta != 0)
                        kept.Add(line);
                }

                return kept;
            }
        }
    }
}
