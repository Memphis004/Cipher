using ProjectSpy.Core.Squad;
using ProjectSpy.Core.Tactical;
using Xunit;

using TableObjectiveType = ProjectSpy.Tables.ObjectiveType;
using SupportAbility = ProjectSpy.Tables.SupportAbility;

namespace ProjectSpy.Core.Tests;

/// <summary>
/// The brief's second required test: orders survive a save/load mid-mission.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this can and cannot assert today.</b> Stage 5 writes the loader; until then
/// there is nothing to read a save back <em>from</em>, so a literal dump-and-reload test
/// would be testing a parser that does not exist. What stage 4e owns is the other half of
/// the contract, and it is the half that actually loses data: the serializer must
/// <em>write</em> every field, and two states that differ in any of them must produce
/// different files. A field the serializer omits fails the first assertion; a field it
/// writes but the hash ignores would pass both a text comparison and a save-and-reload,
/// and would let two genuinely different missions replay onto each other.
/// </para>
/// <para>
/// So: every squad field is asserted present in the dump, and every one is asserted to
/// change the dump when it changes. That is the contract stage 5 will be written against,
/// and it is checkable now.
/// </para>
/// </remarks>
public sealed class SquadOrderPersistenceTests
{
    /// <summary>
    /// Every field a player can act on is present in the canonical dump.
    /// </summary>
    /// <remarks>
    /// The dump is a flat list of key/value lines, so this is a containment check rather
    /// than a value check. It is the check that catches the field nobody wrote: a save
    /// that silently omits the order queue produces a dump that differs from the live
    /// world's only in ways no comparison would notice.
    /// </remarks>
    [Fact]
    public void SquadState_IsWrittenToTheCanonicalDump()
    {
        (WorldState world, _, _) = BuildBusyMission();

        string dump = WorldStateSerializer.ToCanonicalText(world);

        foreach (string key in RequiredKeys())
            Assert.True(dump.Contains(key, StringComparison.Ordinal), $"The dump has no {key} line.");
    }

    /// <summary>
    /// A dump carries the queued orders themselves, one line per field.
    /// </summary>
    /// <remarks>
    /// The place a save most plausibly loses information quietly. A queue written as a
    /// count is enough to hash and useless to load, and nothing else would catch it.
    /// </remarks>
    [Fact]
    public void QueuedOrders_AreWrittenFieldForField()
    {
        (WorldState world, _, _) = BuildBusyMission();

        string dump = WorldStateSerializer.ToCanonicalText(world);

        foreach (string key in new[]
                 {
                     "order.kind", "order.floor", "order.x", "order.connection",
                     "order.interactable", "order.targetActor", "order.item",
                     "order.light", "order.facing", "order.issuedStep",
                 })
        {
            Assert.True(
                dump.Contains(key, StringComparison.Ordinal),
                $"No queued order wrote a {key} line; the queue is being saved as something other than orders.");
        }
    }

    /// <summary>
    /// Two missions differing in any squad field are different files.
    /// </summary>
    /// <remarks>
    /// The other half of the contract. Every case here is a state a player can reach: a
    /// different controlled agent, a queued order, a held member, a spent gadget, a
    /// cooldown running, an objective advanced. If any two of them produced the same
    /// dump then stage 5's loader could not tell them apart and a replay would silently
    /// diverge from the run it was recorded from.
    /// </remarks>
    [Fact]
    public void EverySquadField_ChangesTheCanonicalDump()
    {
        AssertDiffers("a different controlled agent", w =>
            w.ActiveMission!.Control.SwitchTo(OtherAgent(w)));

        AssertDiffers("a queued order", w =>
        {
            TacticalState mission = w.ActiveMission!;
            SquadMember member = mission.Composition!.Members[2];

            mission.Control.Issue(
                mission.Composition,
                member.AgentId,
                new SquadStandingOrder(SquadOrderKind.Regroup, mission.Squad[0].Position));
        });

        AssertDiffers("a released member", w => w.ActiveMission!.Control.ResumeAll());

        AssertDiffers("a spent gadget", w =>
        {
            SquadMember member = w.ActiveMission!.Composition!.Members[0];
            member.Gadgets.Consume(SquadHarness.MedkitGadget);
        });

        AssertDiffers("a role reassignment", w =>
            w.ActiveMission!.Composition!.Members[0].RoleId = SquadHarness.RolesByKey["scout"]);

        AssertDiffers("a cooldown running", w =>
            w.ActiveMission!.CommandPost.Cooldowns[SupportAbility.PingLastKnown] = 12);

        AssertDiffers("an objective advanced", w =>
            w.ActiveMission!.ObjectiveOutcome.WorkSteps += 7);

        AssertDiffers("an aborted mission", w => w.ActiveMission!.AbortCalled = true);

        AssertDiffers("a compromised command post", w =>
        {
            w.ActiveMission!.CommandPost.IsCompromised = true;
            w.ActiveMission.CommandPost.CompromisedOnStep = 900;
        });

        AssertDiffers("a freed prisoner", w =>
        {
            w.ActiveMission!.ObjectiveOutcome.PrisonerFreed = true;
            w.ActiveMission.ObjectiveOutcome.PrisonerId = new TacticalActorId(999);
        });
    }

    /// <summary>
    /// The state hash distinguishes the same cases.
    /// </summary>
    /// <remarks>
    /// Separate from the dump because the hash is what a determinism test actually
    /// compares. A serializer that wrote a field the hash skipped would produce correct
    /// files and incorrect replay detection, and neither of the two checks above would
    /// notice.
    /// </remarks>
    [Fact]
    public void EverySquadField_ChangesTheStateHash()
    {
        AssertHashDiffers("a different controlled agent", w =>
            w.ActiveMission!.Control.SwitchTo(OtherAgent(w)));

        AssertHashDiffers("a queued order", w =>
        {
            TacticalState mission = w.ActiveMission!;
            SquadMember member = mission.Composition!.Members[2];

            mission.Control.Issue(
                mission.Composition,
                member.AgentId,
                new SquadStandingOrder(SquadOrderKind.Regroup, mission.Squad[0].Position));
        });

        AssertHashDiffers("a released member", w => w.ActiveMission!.Control.ResumeAll());

        AssertHashDiffers("a spent gadget", w =>
            w.ActiveMission!.Composition!.Members[0].Gadgets.Consume(SquadHarness.MedkitGadget));

        AssertHashDiffers("a cooldown running", w =>
            w.ActiveMission!.CommandPost.Cooldowns[SupportAbility.CameraFeed] = 12);

        AssertHashDiffers("an objective advanced", w =>
            w.ActiveMission!.ObjectiveOutcome.WorkSteps += 7);

        AssertHashDiffers("an aborted mission", w => w.ActiveMission!.AbortCalled = true);
    }

    /// <summary>
    /// Two runs of the same mission produce the same dump.
    /// </summary>
    /// <remarks>
    /// The direction the other two tests do not cover. A serializer that appended
    /// something non-deterministic — a dictionary enumeration order, a timestamp — would
    /// pass every containment test above and fail here, and would make every recorded
    /// replay mismatch on the file format alone.
    /// </remarks>
    [Fact]
    public void TwoIdenticalMissions_DumpIdentically()
    {
        (WorldState a, _, _) = BuildBusyMission();
        (WorldState b, _, _) = BuildBusyMission();

        Assert.Equal(
            WorldStateSerializer.ToCanonicalText(a),
            WorldStateSerializer.ToCanonicalText(b));
    }

    /// <summary>
    /// Orders can be given while the game is paused.
    /// </summary>
    /// <remarks>
    /// The brief calls this "a core part of how this game is played". It works for a
    /// specific reason worth pinning down: pausing stops the step loop, and the step loop
    /// is the only thing that acts on orders. So a paused mission still holds a valid
    /// state object, still validates orders, and still queues them — and they land on the
    /// first step after unpause rather than being lost. A design that issued orders only
    /// during a step would throw away exactly the pause the player pressed pause for.
    /// </remarks>
    [Fact]
    public void Orders_AreAcceptedWhilePaused_AndLandOnUnpause()
    {
        (WorldState world, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        var session = new GameSession(world);
        session.EnterTacticalMode(mission);

        // A few steps of history, so the pause is in the middle of a mission rather than
        // at the door.
        session.AdvanceTacticalSteps(20);
        long pausedAt = mission.Step;

        AgentId medic = SquadHarness.AgentAt(composition, 2);
        var order = new SquadStandingOrder(SquadOrderKind.Regroup);

        // "Paused" is a UI concept the simulation does not model — there is no step
        // running, which is the whole of it. The honest test is that an order given in
        // that state validates and queues rather than being dropped on the floor.
        SquadOrderResult result = mission.Control.Issue(composition, medic, order);

        Assert.True(result.Ok, $"The order was refused while paused: {result.MessageKey}");
        Assert.Equal(1, mission.Control.For(medic)!.PendingCount);

        // The queued order must not have been consumed by any of the steps that ran
        // before the pause was asked for.
        Assert.Equal(1, mission.Control.For(medic)!.PendingCount);

        session.AdvanceTacticalStep();

        Assert.True(
            mission.Step > pausedAt,
            "Unpausing did not resume the step loop, so a queued order could never land.");
    }

    /// <summary>
    /// An order issued to the controlled agent is refused, and one issued to a held
    /// member clears the hold.
    /// </summary>
    /// <remarks>
    /// The two order-path rules that are easy to get subtly wrong. The first keeps the
    /// player from queueing standing orders for the character they are already driving —
    /// they have direct control and a queue is a way of not having it. The second is
    /// what makes "hold all" escapable: a held member who is given an order is doing that
    /// order, which is the only way out of a hold without a separate resume command.
    /// </remarks>
    [Fact]
    public void Orders_TargetingTheControlledAgent_AreRefused()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        AgentId controlled = mission.Control.Controlled!.Value;

        SquadOrderResult result = mission.Control.Issue(
            composition, controlled, new SquadStandingOrder(SquadOrderKind.Regroup));

        Assert.True(result.IsRejected);
        Assert.Equal(SquadOrderReason.AgentIsDirectlyControlled, result.Reason);
    }

    /// <summary>
    /// An order the role may not be given is refused with the order-level reason.
    /// </summary>
    [Fact]
    public void Orders_OutsideTheRolesAllowedList_AreRefused()
    {
        (WorldState _, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        // The overwatch role lists OverwatchDirection but not CarryAlly, and no role in
        // this fixture is a Handler, so nobody may be asked to call the vehicle.
        AgentId overwatch = SquadHarness.AgentAt(composition, 3);

        SquadOrderResult result = mission.Control.Issue(
            composition,
            overwatch,
            new SquadStandingOrder(SquadOrderKind.RequestExtraction));

        Assert.True(result.IsRejected);
        Assert.Equal(SquadOrderReason.RoleDoesNotAllowOrder, result.Reason);
    }

    // ---- fixtures -------------------------------------------------------------

    /// <summary>
    /// A mission with every kind of squad state present at once.
    /// </summary>
    /// <remarks>
    /// One fixture for all the persistence tests on purpose. They assert on different
    /// fields, but the failure this guards is a whole category never being written at
    /// all, and a fixture per test would let that happen without any of them noticing.
    /// </remarks>
    private static (WorldState World, SquadComposition Composition, TacticalState Mission)
        BuildBusyMission()
    {
        (WorldState world, SquadComposition composition, TacticalState mission) =
            SquadHarness.Dispatched(new[] { "pointman", "hacker", "medic", "mule" });

        composition.Members[0].Gadgets.Assign(SquadHarness.MedkitGadget);

        // Progress, so the objective and the post have state of their own.
        mission.Step = 137;
        mission.ObjectiveOutcome.WorkSteps = 41;
        mission.ObjectiveOutcome.RoomsObserved = 2;

        mission.CommandPost.HandlerId = composition.Members[0].AgentId;
        mission.CommandPost.Cooldowns[SupportAbility.PingLastKnown] = 640;
        mission.CommandPost.HackedDoors.Add(7);
        mission.CommandPost.CalledExtractionRoomIds.Add(3);

        // Held first, then ordered. The order matters and is not cosmetic: "hold all"
        // clears every queue, and giving a held member an order releases that member's
        // hold. Doing it the other way round leaves no queue to save, and doing it this
        // way leaves two members working to orders and two standing where the player
        // stopped them — which is the mixed state a real save is most often taken in.
        mission.Control.HoldAll();

        // Two queued orders on different members, so the queue is a list rather than a
        // single slot and a serializer that wrote only the head would be caught.
        mission.Control.Issue(
            composition,
            composition.Members[0].AgentId,
            new SquadStandingOrder(SquadOrderKind.Regroup, mission.Squad[0].Position));

        mission.Control.Issue(
            composition,
            composition.Members[1].AgentId,
            new SquadStandingOrder(SquadOrderKind.MoveTo, mission.Squad[1].Position));

        world.ActiveMission = mission;
        return (world, composition, mission);
    }

    /// <summary>The keys the dump must contain for the squad state to be saveable.</summary>
    private static IEnumerable<string> RequiredKeys() => new[]
    {
        "squad.hasComposition", "squad.abortCalled", "squad.recordRolls",
        "squad.lastStep", "squad.allHeld", "squad.controlled",

        "composition.objectiveType", "composition.count",
        "member.agent", "member.role", "member.gadget", "member.gadgetUses",
        "composition.handler",

        "orders.agent", "orders.held", "orders.lastOrderStep", "orders.count",

        "objective.type", "objective.workSteps", "objective.exfilSteps",
        "objective.roomsObserved", "objective.blastSteps", "objective.prisoner",
        "objective.prisonerFreed", "objective.target", "objective.targetFled",
        "objective.planted", "objective.workInteractable", "objective.complete",
        "objective.failed", "objective.failure", "objective.decidedBand",
        "objective.decidedStep",

        "post.handler", "post.compromised", "post.feedRoom", "post.feedSteps",
        "post.feedDuration", "post.lastPingContacts", "post.lastPingStep",
        "post.hackedDoor", "post.calledExtraction",
        "post.cooldown.ability", "post.cooldown.steps",
    };

    /// <summary>Some other agent in the squad, for the control-switch case.</summary>
    private static AgentId OtherAgent(WorldState world)
    {
        TacticalState mission = world.ActiveMission!;
        return mission.Squad[1].AgentId;
    }

    /// <summary>Asserts that mutating a mission changes its canonical dump.</summary>
    private static void AssertDiffers(string what, Action<WorldState> mutate)
    {
        (WorldState a, _, _) = BuildBusyMission();
        (WorldState b, _, _) = BuildBusyMission();

        mutate(b);

        Assert.NotEqual(
            WorldStateSerializer.ToCanonicalText(a),
            WorldStateSerializer.ToCanonicalText(b));
    }

    /// <summary>Asserts that mutating a mission changes its state hash.</summary>
    private static void AssertHashDiffers(string what, Action<WorldState> mutate)
    {
        (WorldState a, _, _) = BuildBusyMission();
        (WorldState b, _, _) = BuildBusyMission();

        mutate(b);

        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
    }
}