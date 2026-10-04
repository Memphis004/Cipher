using System.Globalization;
using System.Text;

using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;

namespace ProjectSpy.Core;

/// <summary>
/// A canonical, byte-stable serialization of a <see cref="WorldState"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorldState.ComputeStateHash"/> answers "are these two runs the same?" with
/// 64 bits. That is overwhelmingly sufficient in practice, but it is a hash: in
/// principle two different states can collide, and a determinism test built on a hash
/// cannot tell a collision from a match. The stage-3 brief asks for
/// <em>byte-identical</em> results, so this type exists to make that literal.
/// </para>
/// <para>
/// The format is line-oriented text rather than binary for two reasons. It stays
/// inspectable in a diff when a determinism test fails, which is the moment it matters
/// most; and it forces every field through the same invariant number formatting, so
/// there is no room for a culture-sensitive conversion to differ between machines.
/// </para>
/// <para>
/// <b>Ordering is explicit everywhere.</b> Dictionaries are written in sorted key order
/// and agent collections in id order, because a hash or a comparison over an unordered
/// enumeration would make the output depend on insertion history rather than on state.
/// </para>
/// <para>
/// This is not the stage-5 save format. It has no versioning, no migration and no
/// compression; it is a fingerprint-grade dump whose only job is to be reproducible.
/// Stage 5's save format will be a different, richer thing built on the same discipline.
/// </para>
/// </remarks>
public static class WorldStateSerializer
{
    /// <summary>Serializes a world to canonical text.</summary>
    public static string ToCanonicalText(WorldState world)
    {
        if (world is null) throw new ArgumentNullException(nameof(world));

        var sb = new StringBuilder(16 * 1024);

        sb.Append("world\n");
        Append(sb, "seed", world.Seed);
        Append(sb, "tick", world.Clock.Current.Value);
        Append(sb, "funds", world.Resources.Funds);
        Append(sb, "intel", world.Resources.Intel);
        Append(sb, "materials", world.Resources.Materials);
        Append(sb, "reputation", world.Resources.Reputation);
        Append(sb, "heat", world.Resources.Heat);
        Append(sb, "nextAgentId", world.NextAgentId);
        Append(sb, "nextMissionId", world.NextMissionId);
        Append(sb, "nextContractId", world.NextContractId);
        Append(sb, "lastSettledWeek", world.LastSettledWeek);
        Append(sb, "recruitPoolRefreshTicks", world.RecruitPoolRefreshTicks);
        Append(sb, "recruitPoolGeneratedOnTick", world.RecruitPoolGeneratedOnTick.Value);

        AppendEconomy(sb, world);
        AppendRooms(sb, world);
        AppendAgents(sb, world, world.Agents, "agents");
        AppendAgents(sb, world, world.Recruits, "recruits");
        AppendCounterIntel(sb, world);
        AppendMissions(sb, world);
        AppendTactical(sb, world);
        AppendFlags(sb, world);
        AppendCounters(sb, world);
        AppendRng(sb, world);

        return sb.ToString();
    }

    /// <summary>Serializes a world to canonical UTF-8 bytes.</summary>
    public static byte[] ToCanonicalBytes(WorldState world)
        => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ToCanonicalText(world));

    /// <summary>
    /// Serializes to bytes and folds them into a 64-bit digest.
    /// </summary>
    /// <remarks>
    /// Used as a second, independent equality check alongside
    /// <see cref="WorldState.ComputeStateHash"/>: two different hashes agreeing by
    /// accident is far less likely than one.
    /// </remarks>
    public static ulong ComputeCanonicalDigest(WorldState world)
    {
        byte[] bytes = ToCanonicalBytes(world);

        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }

    // ---- sections ------------------------------------------------------------

    private static void AppendEconomy(StringBuilder sb, WorldState world)
    {
        EconomyState economy = world.Economy;

        sb.Append("economy\n");
        Append(sb, "daysInDeficit", economy.DaysInDeficit);
        Append(sb, "penaltyStage", economy.PenaltyStage);
        Append(sb, "totalInterestPaid", economy.TotalInterestPaid);
        Append(sb, "totalSalariesPaid", economy.TotalSalariesPaid);
        Append(sb, "totalUpkeepPaid", economy.TotalUpkeepPaid);
        Append(sb, "status", (int)economy.Status(world));

        foreach (LoanState loan in economy.Loans)
        {
            Append(sb, "loan.tier", loan.TierId);
            Append(sb, "loan.principal", loan.Principal);
            Append(sb, "loan.outstanding", loan.Outstanding);
            Append(sb, "loan.interestPercent", loan.WeeklyInterestPercent);
            Append(sb, "loan.accrued", loan.AccruedInterest);
            Append(sb, "loan.missed", loan.MissedSettlements);
        }
    }

    private static void AppendRooms(StringBuilder sb, WorldState world)
    {
        sb.Append("rooms\n");

        foreach (Room room in world.BaseLayout.Rooms.OrderBy(r => r.Id.Value))
        {
            Append(sb, "room.id", room.Id.Value);
            Append(sb, "room.type", room.TypeId);
            Append(sb, "room.layer", room.Layer);

            // Sorted: HashSet enumeration order is not guaranteed stable across
            // runtimes, and a canonical dump whose bytes move on their own is not
            // canonical.
            Append(sb, "room.slots", string.Join(",", room.SlotIndices.OrderBy(s => s)));
            Append(sb, "room.adjacent", string.Join(",", room.AdjacentRoomIds.Select(n => n.Value).OrderBy(v => v)));
            Append(sb, "room.level", room.Level);
            Append(sb, "room.condition", room.Condition);
            Append(sb, "room.construction", room.ConstructionTicksRemaining);
            Append(sb, "room.occupants", string.Join(",", room.AssignedAgentIds.Select(a => a.Value).OrderBy(v => v)));
        }
    }

    private static void AppendAgents(StringBuilder sb, WorldState world, IReadOnlyDictionary<AgentId, Agent> source, string section)
    {
        sb.Append(section).Append('\n');

        foreach (Agent agent in source.Values.OrderBy(a => a.Id.Value))
        {
            Append(sb, "agent.id", agent.Id.Value);
            Append(sb, "agent.class", agent.ClassId);
            Append(sb, "agent.name", agent.Name);
            Append(sb, "agent.codename", agent.Codename);
            Append(sb, "agent.level", agent.Level);
            Append(sb, "agent.exp", agent.Exp);
            Append(sb, "agent.infiltration", agent.Skills.Infiltration);
            Append(sb, "agent.combat", agent.Skills.Combat);
            Append(sb, "agent.tech", agent.Skills.Tech);
            Append(sb, "agent.social", agent.Skills.Social);
            Append(sb, "agent.nerve", agent.Skills.Nerve);
            Append(sb, "agent.physical", agent.PhysicalStamina);
            Append(sb, "agent.mental", agent.MentalStamina);
            Append(sb, "agent.loyalty", agent.Loyalty);
            Append(sb, "agent.status", (int)agent.Status);
            Append(sb, "agent.room", agent.AssignedRoomId);
            Append(sb, "agent.salary", agent.SalaryPerWeek);
            Append(sb, "agent.injury", agent.InjurySeverity);
            Append(sb, "agent.burntOut", agent.IsBurntOut ? 1 : 0);
            Append(sb, "agent.burnoutTicks", agent.BurnoutRecoveryTicks);
            Append(sb, "agent.cooldown", agent.LoyaltyEscalationCooldown);
            Append(sb, "agent.escalation", (int)agent.LastEscalation);
            Append(sb, "agent.missions", agent.MissionsCompleted);

            // Both trait lists are included, and sorted. The undiscovered list is
            // simulation state: two worlds that differ only in who is secretly a mole
            // are different worlds, and the determinism test has to say so.
            Append(sb, "agent.traits", string.Join(",", agent.TraitIds.OrderBy(t => t)));
            Append(sb, "agent.hiddenTraits", string.Join(",", agent.UndiscoveredTraitIds.OrderBy(t => t)));
        }

        _ = world;
    }

    private static void AppendCounterIntel(StringBuilder sb, WorldState world)
    {
        sb.Append("counterIntel\n");
        Append(sb, "intel.exposed", world.CounterIntel.ExposedCount);
        Append(sb, "intel.wrongAccusations", world.CounterIntel.WrongAccusations);

        foreach (InvestigationState investigation in world.CounterIntel.Investigations
                     .OrderBy(i => i.SubjectId.Value))
        {
            Append(sb, "investigation.subject", investigation.SubjectId.Value);
            Append(sb, "investigation.evidence", investigation.Evidence);
            Append(sb, "investigation.progress", investigation.ProgressTicks);
            Append(sb, "investigation.steps", investigation.StepsCompleted);
            Append(sb, "investigation.falseLeads", investigation.FalseLeads);
            Append(sb, "investigation.status", (int)investigation.Status);
        }

        foreach (MoleLeak leak in world.CounterIntel.LeakLog)
        {
            Append(sb, "leak.tick", leak.Tick.Value);
            Append(sb, "leak.mission", leak.MissionId);
            Append(sb, "leak.agent", leak.AgentId.Value);
            Append(sb, "leak.heat", leak.HeatAdded);
            Append(sb, "leak.difficulty", leak.DifficultyBonus);
            Append(sb, "leak.public", leak.IsPublic ? 1 : 0);
        }
    }

    private static void AppendMissions(StringBuilder sb, WorldState world)
    {
        sb.Append("missions\n");

        foreach (MissionState mission in world.ActiveMissions.Values.OrderBy(m => m.Id))
        {
            Append(sb, "mission.id", mission.Id);
            Append(sb, "mission.type", mission.TypeId);
            Append(sb, "mission.start", mission.StartTick.Value);
            Append(sb, "mission.end", mission.EstimatedEndTick.Value);
            Append(sb, "mission.outcome", (int)mission.Outcome);
            Append(sb, "mission.agents", string.Join(",", mission.AgentIds.Select(a => a.Value).OrderBy(v => v)));
        }

        foreach (ContractState contract in world.Contracts.Values.OrderBy(c => c.Id))
        {
            Append(sb, "contract.id", contract.Id);
            Append(sb, "contract.type", contract.TypeId);
            Append(sb, "contract.accepted", contract.Accepted ? 1 : 0);
            Append(sb, "contract.dispatched", contract.Dispatched ? 1 : 0);
        }
    }

    /// <summary>
    /// Writes the stage-4e squad state: who was dispatched, in what role, what they are
    /// carrying, who is being driven, what is queued, what the post can do, and how the
    /// objective is going.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the save contract the "orders survive a save mid-mission" test rests
    /// on.</b> Queued orders in particular are the part that is easy to leave out and
    /// impossible to notice: a save that drops the queue loads a mission where a Medic
    /// the player told to go and help somebody quietly does nothing, and nothing in the
    /// loaded file says why.
    /// </para>
    /// <para>
    /// Everything here is a value — an id, a count, an enum, an integer. Nothing writes a
    /// role row or a gadget row out, because those are table data that the loaded build
    /// resolves by id; a save that embedded them would be a save that could not survive a
    /// balance change.
    /// </para>
    /// </remarks>
    private static void AppendSquad(StringBuilder sb, TacticalState mission)
    {
        Append(sb, "squad.hasComposition", mission.Composition is not null ? 1 : 0);
        Append(sb, "squad.abortCalled", mission.AbortCalled ? 1 : 0);
        Append(sb, "squad.lastAbortStep", mission.LastAbortStep);
        Append(sb, "squad.recordRolls", mission.RecordRolls ? 1 : 0);
        Append(sb, "squad.lastStep", mission.Control.LastStep);
        Append(sb, "squad.allHeld", mission.Control.AllHeld ? 1 : 0);
        Append(sb, "squad.controlled", mission.Control.Controlled?.Value ?? -1);

        ObjectiveOutcome outcome = mission.ObjectiveOutcome;
        Append(sb, "objective.type", (int)outcome.Type);
        Append(sb, "objective.nameKey", outcome.NameKey);
        Append(sb, "objective.workSteps", outcome.WorkSteps);
        Append(sb, "objective.exfilSteps", outcome.ExfilSteps);
        Append(sb, "objective.roomsObserved", outcome.RoomsObserved);
        Append(sb, "objective.blastSteps", outcome.BlastStepsRemaining);
        Append(sb, "objective.prisoner", outcome.PrisonerId.Value);
        Append(sb, "objective.prisonerFreed", outcome.PrisonerFreed ? 1 : 0);
        Append(sb, "objective.target", outcome.TargetId.Value);
        Append(sb, "objective.targetFled", outcome.TargetFled ? 1 : 0);
        Append(sb, "objective.planted", outcome.PlantSucceeded ? 1 : 0);
        Append(sb, "objective.workInteractable", outcome.WorkInteractableId);
        Append(sb, "objective.complete", outcome.IsComplete ? 1 : 0);
        Append(sb, "objective.failed", outcome.IsFailed ? 1 : 0);
        Append(sb, "objective.failure", (int)outcome.Failure);
        Append(sb, "objective.decidedBand", (int)outcome.DecidedAtBand);
        Append(sb, "objective.decidedStep", outcome.DecidedOnStep);

        CommandPostState post = mission.CommandPost;
        Append(sb, "post.handler", post.HandlerId?.Value ?? -1);
        Append(sb, "post.compromised", post.IsCompromised ? 1 : 0);
        Append(sb, "post.compromisedStep", post.CompromisedOnStep);
        Append(sb, "post.handlerStepsLeft", post.StepsUntilHandlerTaken);
        Append(sb, "post.feedRoom", post.FeedRoomId ?? -1);
        Append(sb, "post.feedSteps", post.FeedStepsRemaining);
        Append(sb, "post.feedDuration", post.FeedDurationSteps);
        Append(sb, "post.lastPingContacts", post.LastPingContacts);
        Append(sb, "post.lastPingStep", post.LastPingStep);

        foreach (int door in post.HackedDoors.OrderBy(d => d))
            Append(sb, "post.hackedDoor", door);

        foreach (int room in post.CalledExtractionRoomIds.OrderBy(r => r))
            Append(sb, "post.calledExtraction", room);

        foreach (KeyValuePair<ProjectSpy.Tables.SupportAbility, int> cooldown
                 in post.Cooldowns.OrderBy(c => (int)c.Key))
        {
            Append(sb, "post.cooldown.ability", (int)cooldown.Key);
            Append(sb, "post.cooldown.steps", cooldown.Value);
        }

        if (mission.Composition is { } composition)
        {
            Append(sb, "composition.objectiveType", (int)composition.ObjectiveType);
            Append(sb, "composition.count", composition.Members.Count);

            foreach (SquadMember member in composition.Members)
            {
                Append(sb, "member.agent", member.AgentId.Value);
                Append(sb, "member.role", member.RoleId);

                foreach ((int gadgetId, int uses) in member.Gadgets.Entries)
                {
                    Append(sb, "member.gadget", gadgetId);
                    Append(sb, "member.gadgetUses", uses);
                }
            }

            Append(sb, "composition.handler", composition.CommandPostHandler?.Value ?? -1);
        }

        foreach (SquadMemberOrders orders in mission.Control.Members)
        {
            Append(sb, "orders.agent", orders.AgentId.Value);
            Append(sb, "orders.held", orders.Held ? 1 : 0);
            Append(sb, "orders.lastOrderStep", orders.LastOrderStep);
            Append(sb, "orders.count", orders.Queue.Count);

            foreach (SquadStandingOrder order in orders.Queue)
            {
                Append(sb, "order.kind", (int)order.Kind);
                Append(sb, "order.floor", order.Target.FloorIndex);
                Append(sb, "order.x", order.Target.X.Raw);
                Append(sb, "order.connection", order.ConnectionId.Value);
                Append(sb, "order.interactable", order.InteractableId);
                Append(sb, "order.targetActor", order.TargetActorId.Value);
                Append(sb, "order.item", order.ItemId);
                Append(sb, "order.light", order.LightId);
                Append(sb, "order.facing", (int)order.Facing);
                Append(sb, "order.issuedStep", order.IssuedOnStep);
            }
        }
    }

    private static void AppendTactical(StringBuilder sb, WorldState world)
    {
        sb.Append("tactical\n");

        // Explicit rather than conditional on a null ActiveMission: a world with no
        // mission and a dump that simply omits the section look identical, and the
        // omission would be invisible in a diff of a failing determinism test.
        Append(sb, "tactical.active", world.ActiveMission is not null ? 1 : 0);

        if (world.ActiveMission is not null)
        {
            Append(sb, "tactical.mission", world.ActiveMission.MissionId);
            Append(sb, "tactical.site", world.ActiveMission.SiteId);
            Append(sb, "tactical.startedOn", world.ActiveMission.StartedOnTick.Value);
            Append(sb, "tactical.step", world.ActiveMission.Step);
            Append(sb, "tactical.timeConverted", world.ActiveMission.TimeConverted ? 1 : 0);

            AppendSquad(sb, world.ActiveMission);
        }

        foreach (SleeperOperation sleeper in world.SleeperOperations.OrderBy(s => s.AgentId.Value))
        {
            Append(sb, "sleeper.agent", sleeper.AgentId.Value);
            Append(sb, "sleeper.site", sleeper.SiteId);
            Append(sb, "sleeper.startedOn", sleeper.StartedOnTick.Value);
            Append(sb, "sleeper.intelPercent", sleeper.IntelPercent);
            Append(sb, "sleeper.status", (int)sleeper.Status);
            Append(sb, "sleeper.discoveredOn", sleeper.DiscoveredOnTick?.Value ?? -1L);
            Append(sb, "sleeper.intelProgressHundredths", sleeper.IntelProgressHundredths);
            Append(sb, "sleeper.snapshotOn", sleeper.SnapshotOnTick?.Value ?? -1L);
        }

        foreach (IntelSnapshot snapshot in world.IntelSnapshots.Values.OrderBy(s => s.SiteId))
        {
            Append(sb, "intelSnapshot.site", snapshot.SiteId);
            Append(sb, "intelSnapshot.takenOn", snapshot.TakenOnTick.Value);
            Append(sb, "intelSnapshot.intelPercent", snapshot.IntelPercent);
            Append(sb, "intelSnapshot.poisoned", snapshot.IsPoisoned ? 1 : 0);
            Append(sb, "intelSnapshot.band", (int)snapshot.Band);
            Append(sb, "intelSnapshot.mapSeed", snapshot.MapSeed);
            Append(sb, "intelSnapshot.entrance", snapshot.ClaimedEntrance?.Value ?? -1);
            Append(sb, "intelSnapshot.objective", snapshot.ClaimedObjective?.Value ?? -1);
            Append(sb, "intelSnapshot.extraction", string.Join(",", snapshot.ClaimedExtraction.Select(r => r.Value)));
            Append(sb, "intelSnapshot.entryCount", snapshot.Entries.Count);

            foreach (IntelEntry entry in snapshot.Entries)
            {
                Append(sb, "intelEntry.factKind", (int)entry.FactKind);
                Append(sb, "intelEntry.confidence", (int)entry.Confidence);
                Append(sb, "intelEntry.source", (int)entry.Source);

                switch (entry)
                {
                    case IntelRoomEntry room:
                        Append(sb, "intelRoom.id", room.RoomId.Value);
                        Append(sb, "intelRoom.floor", room.FloorIndex);
                        Append(sb, "intelRoom.startX", room.StartX.Raw);
                        Append(sb, "intelRoom.endX", room.EndX.Raw);
                        Append(sb, "intelRoom.nameKey", room.NameKey);
                        Append(sb, "intelRoom.templateId", room.RoomTemplateId);
                        Append(sb, "intelRoom.guardCount", room.GuardCount);
                        Append(sb, "intelRoom.civilianCount", room.CivilianCount);
                        Append(sb, "intelRoom.lightLevel", (int)room.LightLevel);
                        Append(sb, "intelRoom.lightLevelKnown", room.LightLevelKnown ? 1 : 0);
                        Append(sb, "intelRoom.roles", (int)room.Roles);
                        break;

                    case IntelConnectionEntry connection:
                        Append(sb, "intelConnection.id", connection.ConnectionId.Value);
                        Append(sb, "intelConnection.roomA", connection.RoomA.Value);
                        Append(sb, "intelConnection.floorA", connection.FloorIndexA);
                        Append(sb, "intelConnection.roomB", connection.RoomB.Value);
                        Append(sb, "intelConnection.floorB", connection.FloorIndexB);
                        Append(sb, "intelConnection.kind", (int)connection.Kind);
                        Append(sb, "intelConnection.isVertical", connection.IsVertical ? 1 : 0);
                        Append(sb, "intelConnection.isLocked", connection.IsLocked ? 1 : 0);
                        break;

                    case IntelPatrolEntry patrol:
                        Append(sb, "intelPatrol.guard", patrol.GuardId.Value);
                        Append(sb, "intelPatrol.archetype", patrol.ArchetypeId);
                        Append(sb, "intelPatrol.role", (int)patrol.Role);
                        Append(sb, "intelPatrol.home", patrol.HomeRoomId.Value);
                        Append(sb, "intelPatrol.route", string.Join(",", patrol.Route.Select(r => r.Value)));
                        break;

                    case IntelHoldingEntry holding:
                        Append(sb, "intelHolding.agent", holding.AgentId.Value);
                        Append(sb, "intelHolding.room", holding.RoomId.Value);
                        Append(sb, "intelHolding.ticksUntilLost", holding.TicksUntilLost);
                        break;
                }
            }
        }

        foreach (CaptureRecord capture in world.Captures.OrderBy(c => c.AgentId.Value))
        {
            Append(sb, "capture.agent", capture.AgentId.Value);
            Append(sb, "capture.site", capture.CaptureSiteId);
            Append(sb, "capture.hostSite", capture.HostSiteId);
            Append(sb, "capture.capturedOn", capture.CapturedOnTick.Value);
            Append(sb, "capture.ticksUntilLost", capture.TicksUntilLost);
        }
    }

    private static void AppendFlags(StringBuilder sb, WorldState world)
    {
        sb.Append("flags\n");

        foreach (KeyValuePair<string, bool> flag in world.Flags.OrderBy(f => f.Key, StringComparer.Ordinal))
            Append(sb, "flag", $"{flag.Key}={(flag.Value ? 1 : 0)}");
    }

    private static void AppendCounters(StringBuilder sb, WorldState world)
    {
        sb.Append("counters\n");

        foreach (KeyValuePair<string, int> counter in world.Counters.OrderBy(c => c.Key, StringComparer.Ordinal))
            Append(sb, "counter", $"{counter.Key}={counter.Value}");
    }

    private static void AppendRng(StringBuilder sb, WorldState world)
    {
        sb.Append("rng\n");
        RngStreamsState state = world.RngStreams.SaveState();
        Append(sb, "rng.rootSeed", state.RootSeed);

        foreach (RngState stream in state.Streams)
        {
            Append(sb, "rng.s0", stream.S0);
            Append(sb, "rng.s1", stream.S1);
            Append(sb, "rng.s2", stream.S2);
            Append(sb, "rng.s3", stream.S3);
        }
    }

    /// <summary>
    /// Writes one key/value line.
    /// </summary>
    /// <remarks>
    /// Strings are emitted through the invariant culture and with newlines escaped, so a
    /// candidate name containing a line break cannot forge an extra field and make two
    /// different states serialize identically.
    /// </remarks>
    private static void Append(StringBuilder sb, string key, object value)
    {
        string text = value switch
        {
            long l => l.ToString(CultureInfo.InvariantCulture),
            ulong u => u.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            uint u => u.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            _ => Escape(value.ToString() ?? string.Empty),
        };

        sb.Append(key).Append('=').Append(text).Append('\n');
    }

    private static string Escape(string raw)
    {
        var sb = new StringBuilder(raw.Length + 8);

        foreach (char c in raw)
        {
            if (c is '\n' or '\r' or '\\')
            {
                sb.Append('\\').Append(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}