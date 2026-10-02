namespace ProjectSpy.Sim;

/// <summary>
/// Entry point for the headless simulator.
/// </summary>
/// <remarks>
/// Stage 6 adds three subcommands: <c>play</c> (an interactive text client),
/// <c>sim</c> (N autonomous agencies over M days) and <c>verify</c> (replay checking).
/// Until then each one throws explicitly rather than pretending to work.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0] : string.Empty;

        try
        {
            return command switch
            {
                // TODO(stage-6): interactive text-mode client — print base status,
                // roster, contracts and missions; map typed input 1:1 to ICommand.
                "play" => throw new NotImplementedException(
                    "TODO(stage-6): implement the interactive play client."),

                // TODO(stage-6): run N agencies for M days under a scripted policy and
                // emit CSV plus a markdown balance report.
                "sim" => throw new NotImplementedException(
                    "TODO(stage-6): implement the batch balance simulator."),

                // TODO(stage-6): run ReplayVerifier over a directory of replay files.
                "verify" => throw new NotImplementedException(
                    "TODO(stage-6): implement the replay verifier runner."),

                _ => Usage(),
            };
        }
        catch (NotImplementedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("ProjectSpy.Sim — headless balance harness (arrives in stage 6)");
        Console.WriteLine();
        Console.WriteLine("Usage: ProjectSpy.Sim <command>");
        Console.WriteLine("  play    — interactive text-mode client");
        Console.WriteLine("  sim     — batch simulation over many agencies");
        Console.WriteLine("  verify  — verify recorded replays");
        return 1;
    }
}
