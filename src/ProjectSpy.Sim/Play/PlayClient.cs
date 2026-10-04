using System.Globalization;

using ProjectSpy.Core;
using ProjectSpy.Core.Squad;

using ProjectSpy.Sim.Batch;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using TableSiteTemplate = ProjectSpy.Tables.SiteTemplate;

namespace ProjectSpy.Sim.Play;

/// <summary>
/// The <c>play</c> subcommand: what a person sits down in front of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Choosing the mission is part of the game.</b> A client that hardcodes "tier 3,
/// StealData" would prove the tactical layer plays and prove nothing about whether a
/// player can tell a tier 1 from a tier 4 before committing. So the picker shows the
/// template table the way a contract board would, and the player picks.
/// </para>
/// <para>
/// <b>Every choice is overridable from the command line.</b> <c>--template</c>,
/// <c>--objective</c> and <c>--seed</c> exist so a session can be reproduced exactly,
/// which is the one thing a play client has to be able to do for its own bug reports to
/// mean anything.
/// </para>
/// </remarks>
public static class PlayClient
{
    private static readonly TableObjectiveType[] Objectives = Enum.GetValues<TableObjectiveType>();

    public static int Run(string[] args)
    {
        if (!SimulationRules.AreTablesLoaded)
            throw new InvalidOperationException(
                "Tables did not load, so there is nothing to play (knowledge.md rule 3). Run tools/gen.ps1.");

        string mode = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0].ToLowerInvariant()
            : string.Empty;

        string[] rest = mode.Length == 0 ? args : args[1..];

        return mode switch
        {
            "tactical" or "tac" => Tactical(rest),
            "strategic" or "strat" => Strategic(rest),
            "" => Menu(rest),
            _ => Unknown(mode),
        };
    }

    private static int Unknown(string mode)
    {
        Console.Error.WriteLine($"'play {mode}' is not a mode. Try: play, play tactical, play strategic.");
        return 1;
    }

    /// <summary>
    /// Picks a mission by hand, then plays it.
    /// </summary>
    /// <remarks>
    /// Refuses to run without a terminal rather than silently hanging on
    /// <see cref="Console.ReadLine"/>. A developer piping a script into <c>play</c> with no
    /// arguments is waiting for a prompt that will never come.
    /// </remarks>
    private static int Menu(string[] args)
    {
        Options options = Options.Parse(args);
        IReadOnlyList<TableSiteTemplate> templates = SimulationRules.AllSiteTemplates();

        Console.WriteLine();
        Console.WriteLine("  ================= play =================");
        Console.WriteLine();
        Console.WriteLine("  which mission?");

        int index = 0;

        foreach (TableSiteTemplate template in templates)
        {
            Console.WriteLine(
                $"   {++index,2}.  tier {template.Tier}  {Shorten(template.NameKey),-22}"
                + $" floors {template.FloorCountMin}-{template.FloorCountMax}"
                + $"  guards {template.GuardCountMin}-{template.GuardCountMax}"
                + $"  security {template.SecurityGrade}");
        }

        Console.WriteLine();
        Console.WriteLine("   objective: " + string.Join(", ", Objectives.Select(o => o.ToString().ToLowerInvariant())));
        Console.WriteLine();

        if (!TryRead(out string? line) || line is null)
        {
            Console.Error.WriteLine(
                "no terminal to read a choice from. pass --template <id> --objective <name> --seed <n> instead,"
                + " or use 'play tactical --help'.");
            return 1;
        }

        string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int wanted)
            || wanted < 1 || wanted > templates.Count)
        {
            Console.Error.WriteLine($"'{line.Trim()}' is not one of the {templates.Count} missions.");
            return 1;
        }

        TableSiteTemplate chosen = templates[wanted - 1];
        TableObjectiveType objective = ParseObjective(parts.Length > 1 ? parts[1] : null);
        ulong seed = options.Has("seed")
            ? (ulong)options.Int("seed", 1)
            : (ulong)Environment.TickCount64;

        return PlayChosen(chosen, objective, seed, options);
    }

    /// <summary>Plays one mission with everything named on the command line.</summary>
    private static int Tactical(string[] args)
    {
        Options options = Options.Parse(args);
        IReadOnlyList<TableSiteTemplate> templates = SimulationRules.AllSiteTemplates();

        TableSiteTemplate? chosen = options.Has("template")
            ? SimulationRules.SiteTemplateFor(options.Int("template", 0))
            : null;

        // The site table fixes the tier; letting the command line disagree with it would
        // hand the generator a (template, tier) pair that never appears in the game and
        // then report the result as if it had.
        TableSiteTemplate template = chosen ?? templates[0];

        TableObjectiveType objective = ParseObjective(options.String("objective", "stealdata"));
        ulong seed = (ulong)options.Int("seed", 1);
        int missionId = options.Int("mission", 1);

        SquadPolicy policy = ParsePolicy(options.String("policy", "stealth"));
        bool autoPilot = options.Has("auto");

        return PlayChosen(template, objective, seed, options, policy, autoPilot, missionId);
    }

    private static int PlayChosen(
        TableSiteTemplate template,
        TableObjectiveType objective,
        ulong seed,
        Options options,
        SquadPolicy? policy = null,
        bool autoPilot = false,
        int missionId = 1)
    {
        MissionFixture fixture = MissionFixture.Create(
            seed, template.Id, template.Tier, objective, missionId,
            MissionFixture.RolesFor(objective));

        var client = new TacticalClient(Console.Out, Console.In, fixture, autoPilot);

        if (policy is not null)
            client.Policy = policy.Value;

        client.Run();
        return 0;
    }

    /// <summary>The strategic half. Not built yet — see the note below.</summary>
    // TODO(stage-6): build the strategic client. The throw below is deliberate and must
    // stay until WorldState.Contracts and WorldState.ActiveMissions have a producer;
    // rule 5 wants an unimplemented command to fail loudly rather than open an empty board.
    private static int Strategic(string[] args)
        => throw new NotImplementedException(
            "TODO(stage-6): the strategic client — base, roster, rooms, contracts, sleeper "
            + "operations, advance tick. Nothing in the codebase populates WorldState.Contracts "
            + "or WorldState.ActiveMissions yet, so a strategic view built today would show "
            + "an empty board over a real clock and would prove nothing. Play a mission first: "
            + "'play tactical'.");

    private static TableObjectiveType ParseObjective(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TableObjectiveType.StealData;

        string wanted = text.Trim().Replace("_", string.Empty).ToLowerInvariant();

        foreach (TableObjectiveType candidate in Objectives)
        {
            if (string.Equals(candidate.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        throw new ArgumentException(
            $"'{text}' is not an objective. One of: {string.Join(", ", Objectives.Select(o => o.ToString().ToLowerInvariant()))}.");
    }

    private static SquadPolicy ParsePolicy(string text)
    {
        if (!Enum.TryParse(text, ignoreCase: true, out SquadPolicy policy) || !Enum.IsDefined(policy))
            throw new ArgumentException(
                $"'{text}' is not a policy. One of: {string.Join(", ", Enum.GetNames<SquadPolicy>())}.");

        return policy;
    }

    private static bool TryRead(out string? line)
    {
        if (Console.IsInputRedirected)
        {
            line = null;
            return false;
        }

        Console.Write("  > ");
        line = Console.ReadLine();
        return true;
    }

    private static string Shorten(string nameKey)
    {
        int dot = nameKey.LastIndexOf('.');

        return dot >= 0 && dot < nameKey.Length - 1 ? nameKey[(dot + 1)..] : nameKey;
    }
}