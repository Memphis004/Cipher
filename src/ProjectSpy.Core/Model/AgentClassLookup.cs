using ProjectSpy.Tables;

// The generated manager class is named `Tables`, which collides with the
// ProjectSpy.Tables namespace. Because this file lives under ProjectSpy.*, the bare
// name binds to the namespace, so an alias is required.
using GameTables = ProjectSpy.Tables.Tables;

namespace ProjectSpy.Core;

/// <summary>
/// Core's lookups over the <c>agent_class</c> table.
/// </summary>
/// <remarks>
/// The market-rate question — "is this agent paid fairly?" — is answered from the
/// class's <c>salary_base</c>, so the class row has to be reachable from the loyalty
/// pass without dragging Luban's type into it.
/// </remarks>
public static class AgentClasses
{
    private static GameTables? _tables;

    private static GameTables? TablesOrNull
    {
        get
        {
            if (_tables is not null) return _tables;

            try
            {
                _tables = TableService.Load();
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            return _tables;
        }
    }

    /// <summary>The class row for an id, or null when it is not in the table.</summary>
    public static AgentClass? Find(int classId)
    {
        GameTables? tables = TablesOrNull;
        return tables is null ? null : tables.TbAgentClass.GetOrDefault(classId) as AgentClass;
    }

    /// <summary>Every class row, or empty when the tables are unavailable.</summary>
    public static IReadOnlyList<AgentClass> All()
    {
        GameTables? tables = TablesOrNull;
        return tables is null ? Array.Empty<AgentClass>() : tables.TbAgentClass.DataList;
    }

    /// <summary>
    /// The market weekly salary for a class, used to judge pay fairness.
    /// </summary>
    /// <remarks>
    /// Returns zero for an unknown class, which makes every salary look generous and
    /// therefore stops the pay-fairness penalty rather than applying a bogus one.
    /// That is the safer of the two wrong answers: the alternative is draining loyalty
    /// from every agent because of a save referencing a retired class.
    /// </remarks>
    public static int MarketSalaryFor(int classId) => Find(classId)?.SalaryBase ?? 0;

    /// <summary>The base skill vector for a class.</summary>
    public static SkillSet BaseSkillsFor(int classId)
    {
        AgentClass? c = Find(classId);
        return c is null
            ? SkillSet.Zero
            : new SkillSet(c.BaseInfiltration, c.BaseCombat, c.BaseTech, c.BaseSocial, c.BaseNerve);
    }
}

/// <summary>
/// Core's lookups over the <c>trait</c> table.
/// </summary>
/// <remarks>
/// Traits carry their effect as a string in the table (<c>effect_type</c>) and an
/// integer magnitude. That string is compared here rather than in each caller, so
/// adding a trait effect means editing one switch instead of hunting for every place
/// that reads <c>effect_type</c>.
/// </remarks>
public static class TraitLookup
{
    private static GameTables? _tables;

    private static GameTables? TablesOrNull
    {
        get
        {
            if (_tables is not null) return _tables;

            try
            {
                _tables = TableService.Load();
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            return _tables;
        }
    }

    /// <summary>The trait row for an id, or null.</summary>
    public static Trait? Find(int traitId)
    {
        GameTables? tables = TablesOrNull;
        return tables is null ? null : tables.TbTrait.GetOrDefault(traitId) as Trait;
    }

    /// <summary>Every trait row, or empty when the tables are unavailable.</summary>
    public static IReadOnlyList<Trait> All()
    {
        GameTables? tables = TablesOrNull;
        return tables is null ? Array.Empty<Trait>() : tables.TbTrait.DataList;
    }

    /// <summary>Trait ids whose polarity is Hidden (Mole, Deserter, …).</summary>
    public static IReadOnlyList<int> HiddenTraitIds()
    {
        var ids = new List<int>();
        foreach (Trait trait in All())
        {
            if (trait.Polarity == TraitPolarity.Hidden)
                ids.Add(trait.Id);
        }

        return ids;
    }

    /// <summary>Trait ids that are safe to show on a candidate card.</summary>
    public static IReadOnlyList<int> VisibleTraitIds()
    {
        var ids = new List<int>();
        foreach (Trait trait in All())
        {
            if (trait.Polarity != TraitPolarity.Hidden)
                ids.Add(trait.Id);
        }

        return ids;
    }

    /// <summary>The id of the mole trait, resolved from the table rather than hard-coded.</summary>
    public static int MoleTraitId
    {
        get
        {
            foreach (Trait trait in All())
            {
                if (trait.EffectType.Equals("HeatGain", StringComparison.Ordinal))
                    return trait.Id;
            }

            return 0;
        }
    }

    /// <summary>
    /// Sum of <paramref name="effectType"/> magnitudes across an agent's traits,
    /// counting discovered and undiscovered alike.
    /// </summary>
    /// <remarks>
    /// Hidden traits count because they are real. A mole's Heat contribution has to
    /// apply whether or not the player has found them, or uncovering a mole would
    /// hand the player a free damage buff by accident.
    /// </remarks>
    public static int SumEffect(Agent agent, string effectType)
    {
        if (agent is null) throw new ArgumentNullException(nameof(agent));

        int total = 0;
        foreach (int traitId in agent.TraitIds)
            total += EffectValueOf(traitId, effectType);

        foreach (int traitId in agent.UndiscoveredTraitIds)
            total += EffectValueOf(traitId, effectType);

        return total;
    }

    private static int EffectValueOf(int traitId, string effectType)
    {
        Trait? trait = Find(traitId);
        if (trait is null) return 0;

        return trait.EffectType.Equals(effectType, StringComparison.Ordinal)
            ? trait.EffectValue
            : 0;
    }
}

/// <summary>
/// Core's lookup over the <c>agent_name</c> table, used for candidate names.
/// </summary>
/// <remarks>
/// Names are flavour, but they are drawn from a seeded stream so a replay reproduces
/// the same roster by name as well as by id.
/// </remarks>
public static class NameTable
{
    private static GameTables? _tables;

    private static GameTables? TablesOrNull
    {
        get
        {
            if (_tables is not null) return _tables;

            try
            {
                _tables = TableService.Load();
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            return _tables;
        }
    }

    /// <summary>Every name row, or empty when the tables are unavailable.</summary>
    public static IReadOnlyList<AgentName> All()
    {
        GameTables? tables = TablesOrNull;
        return tables is null ? Array.Empty<AgentName>() : tables.TbAgentName.DataList;
    }
}