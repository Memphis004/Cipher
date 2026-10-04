using ProjectSpy.Sim.Batch;

namespace ProjectSpy.Sim;

/// <summary>
/// Entry point for the headless simulator.
/// </summary>
/// <remarks>
/// Three subcommands, and they answer three different questions. <c>play</c> is "is this
/// tense" and only a person can answer it. <c>sim</c> is "does any of this work at
/// scale" and only a machine can answer it. <c>verify</c> is "is it the same run twice",
/// which is the one question that has to keep working after everything else ships.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        try
        {
            return command switch
            {
                "play" => Play(rest),
                "sim" => Sim(rest),
                "diag" => Diag(),
                "doors" => Doors(rest),
                "trace" => PolicyTrace.Execute(rest),
                "verify" => Verify(rest),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is not NotImplementedException)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 3;
        }
        catch (NotImplementedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>Runs the mission balance sweep and writes the report.</summary>
    private static int Sim(string[] args)
    {
        Options options = Options.Parse(args);
        int seeds = options.Int("seeds", 40);
        string outDir = options.String("out", "docs/balance");
        string stamp = options.String("date", DateTime.UtcNow.ToString("yyyy-MM-dd"));

        Console.WriteLine($"ProjectSpy.Sim sim — {seeds} seeds per template, every policy");
        Console.WriteLine();

        var started = DateTime.UtcNow;
        IReadOnlyList<SweepCell> cells = BalanceSweep.Run(seeds);
        IReadOnlyList<BalanceWarning> warnings = BalanceReport.Warnings(cells);

        Console.WriteLine($"played {cells.Sum(c => (long)c.Runs * System.Enum.GetValues<Core.Squad.SquadPolicy>().Length):N0} missions "
            + $"in {(DateTime.UtcNow - started).TotalSeconds:F1}s");
        Console.WriteLine();

        string markdown = BalanceReport.ToMarkdown(cells, warnings);
        string csv = BalanceReport.ToCsv(cells);

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"BALANCE-{stamp}.md"), markdown);
        File.WriteAllText(Path.Combine(outDir, $"balance-{stamp}.csv"), csv);

        foreach (BalanceWarning warning in warnings.OrderByDescending(w => w.Severity == "critical" ? 2 : w.Severity == "high" ? 1 : 0))
            Console.WriteLine($"{warning.Severity,-8} {warning.Key}");

        Console.WriteLine();
        Console.WriteLine($"wrote {outDir}/BALANCE-{stamp}.md");
        Console.WriteLine($"wrote {outDir}/balance-{stamp}.csv");
        return 0;
    }

    /// <summary>
    /// A scratch probe over a few seeds, for diagnosing a sweep rather than reporting it.
    /// </summary>
    private static int Diag()
    {
        Batch.Diag.Run();
        return 0;
    }

    /// <summary>
    /// Measures what locked doors do to generated sites, without changing anything.
    /// </summary>
    /// <remarks>
    /// A subcommand rather than a mode of <c>sim</c> because the two answer different
    /// questions and are run at different times: this one is run before a generator
    /// change to record the number, and again after it to show what the change did.
    /// </remarks>
    private static int Doors(string[] args)
    {
        Options options = Options.Parse(args);
        int sites = options.Int("sites", 40);

        Console.WriteLine($"ProjectSpy.Sim doors — {sites} sites per template, no missions played");
        Console.WriteLine();

        var started = DateTime.UtcNow;
        DoorSurvey.Result result = DoorSurvey.Run(sites);

        Console.WriteLine();
        Console.WriteLine($"sites generated            {result.Sites}");
        Console.WriteLine($"sites with any locked door {result.SitesWithAnyLock} ({result.AnyLockFraction:P1})");
        Console.WriteLine($"objective only via a lock  {result.ObjectiveBehindALock} ({result.ObjectiveLockedFraction:P1})");
        Console.WriteLine($"way out only via a lock    {result.ExtractionBehindALock} ({result.ExtractionLockedFraction:P1})");
        Console.WriteLine(
            $"rooms only via a lock      {result.RoomsOnlyReachableThroughALock}/{result.TotalRooms} "
            + $"({result.RoomLockedFraction:P1})");
        Console.WriteLine(
            $"rooms squad cannot route   {result.RoomsTheSquadCannotRouteTo}/{result.TotalRooms} "
            + $"({result.SquadUnroutableFraction:P1}) to the objective");
        Console.WriteLine(
            $"rooms squad cannot exit    {result.RoomsTheSquadCannotRouteToAnyExit}/{result.TotalRooms} "
            + $"({result.SquadCannotExitFraction:P1})");
        Console.WriteLine($"took {(DateTime.UtcNow - started).TotalSeconds:F1}s");

        return 0;
    }

    private static int Play(string[] args) => ProjectSpy.Sim.Play.PlayClient.Run(args);

    // TODO(stage-6): build the replay verifier. It needs stage 5's ReplayVerifier and the
    // on-disk replay format; both are absent, so this stays a loud failure (rule 5).
    private static int Verify(string[] args)
    {
        throw new NotImplementedException(
            "TODO(stage-6): the replay verifier needs stage 5's ReplayVerifier, which is "
            + "not built. GameSession already has VerifyReplay and Replay, so this is "
            + "a wrapper once the on-disk replay format exists.");
    }

    private static int Usage()
    {
        Console.WriteLine("ProjectSpy.Sim — headless harness");
        Console.WriteLine();
        Console.WriteLine("Usage: ProjectSpy.Sim <command> [options]");
        Console.WriteLine();
        Console.WriteLine("  play    — interactive text client for both scales");
        Console.WriteLine("           (no args)      pick a mission from the site table, then play it");
        Console.WriteLine("  play tactical    — one mission; --template <id> --objective <name> --seed <n>");
        Console.WriteLine("                   --policy <name> --auto   (auto = the squad plays itself)");
        Console.WriteLine("  play strategic  — TODO(stage-6): nothing populates Contracts or ActiveMissions yet");
        Console.WriteLine("  sim     — batch mission sweep; writes markdown + CSV");
        Console.WriteLine("           --seeds <n>     seeds per site template (default 40)");
        Console.WriteLine("           --out <dir>     output directory (default docs/balance)");
        Console.WriteLine("           --date <stamp>  report date stamp (default today, UTC)");
        Console.WriteLine("  verify  — verify recorded replays against a directory");
        Console.WriteLine("  diag    — per-run probe over a few seeds, for debugging a sweep");
        Console.WriteLine("  doors   — measure what locked doors do to generated sites");
        Console.WriteLine("           --sites <n>     sites per site template (default 40)");
        Console.WriteLine("  trace   — what each policy ordered, step by step, and where two differ");
        Console.WriteLine("           --template <id> --objective <name> --seed <n> --window <n> --orders");
        return 1;
    }
}

/// <summary>
/// The `--key value` pairs a subcommand was given.
/// </summary>
/// <remarks>
/// A deliberately tiny parser rather than a library: this is a developer tool with three
/// subcommands and four options, and adding a dependency to parse four strings would be
/// a worse trade than thirty lines. Unknown keys are an error rather than a shrug,
/// because a mistyped <c>--seed</c> silently running forty sweeps is a wasted afternoon.
/// </remarks>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Options Parse(IReadOnlyList<string> args)
    {
        var options = new Options();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{arg}'. Options look like --name value.");

            string name = arg[2..];

            // A flag rather than a pair: --auto, --whatever. Read with Has, not String.
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[name] = string.Empty;
                continue;
            }

            options._values[name] = args[++i];
        }

        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string String(string name, string fallback)
        => _values.TryGetValue(name, out string? value) ? value : fallback;

    public int Int(string name, int fallback)
    {
        if (!_values.TryGetValue(name, out string? value))
            return fallback;

        if (!int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            throw new ArgumentException($"Option --{name} wants a whole number, not '{value}'.");

        return parsed;
    }
}