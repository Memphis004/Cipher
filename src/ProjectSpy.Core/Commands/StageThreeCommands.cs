namespace ProjectSpy.Core;

/// <summary>Reason codes added for stage 3. Values are appended, never renumbered.</summary>
public static class Stage3Reasons
{
    // Declared alongside the enum in ICommand.cs via partial-free constants, so this
    // file only documents them.
}

/// <summary>Reason codes added for stage 3 live in <see cref="CommandReason"/>.</summary>
/// <summary>Takes out a loan of the given tier.</summary>
public sealed record TakeLoanCommand(int TierId) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        foreach (ProjectSpy.Tables.LoanTier tier in SimulationRules.LoanTiers())
        {
            if (tier.Id != TierId)
                continue;

            if (world.Resources.Reputation < tier.ReputationRequired)
            {
                return CommandResult.Rejected(
                    CommandReason.InsufficientReputation,
                    CommandArgs.Shortfall(tier.ReputationRequired, world.Resources.Reputation));
            }

            if (world.Economy.ActiveLoanCount >= tier.MaxActive)
                return CommandResult.Rejected(CommandReason.LoanLimitReached, CommandArgs.Entity(TierId));

            return CommandResult.Ok;
        }

        return CommandResult.Rejected(CommandReason.UnknownLoanTier, CommandArgs.Entity(TierId));
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng)
    {
        if (EconomySystem.TakeLoan(world, TierId, world.Events) is null)
        {
            throw new InvalidOperationException(
                $"TakeLoanCommand passed validation but tier {TierId} could not be issued.");
        }
    }
}

/// <summary>Repays principal against the oldest loan.</summary>
public sealed record RepayLoanCommand(long Amount) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        if (Amount <= 0)
            return CommandResult.Rejected(CommandReason.InvalidAmount, CommandArgs.Amount(Amount));

        if (world.Economy.Loans.Count == 0)
            return CommandResult.Rejected(CommandReason.NoOutstandingLoan);

        if (world.Resources.Funds < Amount)
        {
            return CommandResult.Rejected(
                CommandReason.InsufficientFunds,
                CommandArgs.Shortfall(Amount, world.Resources.Funds));
        }

        return CommandResult.Ok;
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng) => EconomySystem.RepayLoan(world, Amount);
}

/// <summary>Opens a counter-intelligence investigation against an agent.</summary>
public sealed record StartInvestigationCommand(AgentId SubjectId) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        if (world.GetAgent(SubjectId) is null)
            return CommandResult.Rejected(CommandReason.UnknownAgent, CommandArgs.Entity(SubjectId.Value));

        if (world.CounterIntel.FindFor(SubjectId) is { IsRunning: true })
            return CommandResult.Rejected(CommandReason.InvestigationAlreadyRunning, CommandArgs.Entity(SubjectId.Value));

        if (MoleSystem.Capacity(world) <= 0)
            return CommandResult.Rejected(CommandReason.NoCounterIntelCapacity);

        return CommandResult.Ok;
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng)
    {
        if (MoleSystem.StartInvestigation(world, SubjectId, world.Events) is null)
        {
            throw new InvalidOperationException(
                $"StartInvestigationCommand passed validation but no investigation opened for {SubjectId}.");
        }
    }
}

/// <summary>Names an agent as the mole, right or wrong.</summary>
public sealed record AccuseAgentCommand(AgentId SubjectId) : ICommand
{
    /// <inheritdoc />
    public CommandResult Validate(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        if (world.GetAgent(SubjectId) is null)
            return CommandResult.Rejected(CommandReason.UnknownAgent, CommandArgs.Entity(SubjectId.Value));

        // No evidence requirement: accusing on a hunch is allowed, because being wrong
        // is a designed cost rather than a prevented state. Requiring proof would remove
        // the risk that makes counter-intel a decision instead of a formality.
        return CommandResult.Ok;
    }

    /// <inheritdoc />
    public void Apply(WorldState world, IRng rng) => MoleSystem.Accuse(world, SubjectId, world.Events);
}