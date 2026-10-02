// Stage 2 fallback: a MINIMAL hand-written table service so ProjectSpy.Lib can
// compile before Luban has ever run (no generated Gen/ output exists yet).
//
// DELETE-ON-GENERATE: once `pwsh tools/gen.ps1` has produced
// src/ProjectSpy.Tables/Gen, the generated Tables class replaces this facade —
// remove this file and switch ProjectSpy.Lib to the generated beans (same field
// names, same int-keyed lookups).
//
// Row shapes mirror data/*.csv column-for-column (see data/README.md for the
// schema conventions and data/__tables__.csv for the table registry).

namespace ProjectSpy.Tables;

/// <summary>data/item.csv — one row per item (29 rows: baits 1001-1003, rods 2001-2003,
/// fish 3001-3008, seeds 4001-4003, crops 5001-5003, materials 6001-6003, foods 7001-7006).</summary>
public sealed record ItemRow(int Id, string NameKey, string Category, int BasePrice, bool Stackable);

/// <summary>data/fish.csv — one row per fish species (8 rows). BaseWeight is the
/// per-pond relative draw weight used by the on-chain weighted roll.</summary>
public sealed record FishRow(int Id, int ItemId, int Rarity, int RequiredLevel, int BaseWeight,
    float MinSize, float MaxSize, int ExpReward);

/// <summary>data/bait.csv — hit/rare bonuses granted while this bait is equipped.</summary>
public sealed record BaitRow(int Id, int ItemId, int HitBonus, int RareBonus);

/// <summary>data/rod.csv — bonuses and the stamina discount subtracted from the
/// fishing cost (max(1, 5 - StaminaDiscount)).</summary>
public sealed record RodRow(int Id, int ItemId, int HitBonus, int RareBonus, int StaminaDiscount);

/// <summary>data/pond.csv — village pond definition. FishPool lists fish.id values
/// (NOT item ids); OccupyBlocks drives slot expiry (knowledge.md rule 3).</summary>
public sealed record PondRow(int Id, string NameKey, int SceneId, int RequiredLevel, int BaseHitRate,
    string FishPool, int SlotCount, int OccupyBlocks);

/// <summary>data/recipe.csv — normal + "great" result pair and stamina restore values.</summary>
public sealed record RecipeRow(int Id, string NameKey, int RequiredLevel, int StaminaCost,
    int ResultItemId, int GreatResultItemId, int ExpReward, int StaminaRestore, int GreatStaminaRestore);

/// <summary>data/recipe_material.csv — one required material per row.</summary>
public sealed record RecipeMaterialRow(int Id, int RecipeId, int ItemId, int Count);

/// <summary>data/seed.csv — GrowBlocks is evaluated lazily at harvest (knowledge.md rule 3).</summary>
public sealed record SeedRow(int Id, int SeedItemId, int CropItemId, int GrowBlocks, int StaminaCost,
    int YieldMin, int YieldMax);

/// <summary>data/shop.csv — fixed-price NPC shop entry (DailyStock 0 = unlimited).</summary>
public sealed record ShopRow(int Id, int ItemId, int Price, int DailyStock, int RequiredLevel);

/// <summary>data/task.csv — one daily taskboard task.</summary>
public sealed record TaskRow(int Id, string Type, int TargetItemId, int TargetCount,
    int RequiredLevel, int RewardGold, int RewardExp, int RewardItemId, int RewardItemCount);

/// <summary>data/level_exp.csv — exp required to REACH Level from the previous level.</summary>
public sealed record LevelExpRow(int Level, int RequiredExp);

/// <summary>
/// Minimal read-only view over the game tables. Stage 2 fallback implementation
/// below returns empty tables; tools/gen.ps1 + the Luban-generated Tables class
/// replace it once the pipeline has run.
/// </summary>
public interface ITableService
{
    IReadOnlyDictionary<int, ItemRow> Items { get; }
    IReadOnlyDictionary<int, FishRow> Fish { get; }
    IReadOnlyDictionary<int, BaitRow> Baits { get; }
    IReadOnlyDictionary<int, RodRow> Rods { get; }
    IReadOnlyDictionary<int, PondRow> Ponds { get; }
    IReadOnlyDictionary<int, RecipeRow> Recipes { get; }
    IReadOnlyList<RecipeMaterialRow> RecipeMaterials { get; }
    IReadOnlyDictionary<int, SeedRow> Seeds { get; }
    IReadOnlyDictionary<int, ShopRow> Shop { get; }
    IReadOnlyList<TaskRow> Tasks { get; }
    IReadOnlyList<LevelExpRow> LevelExp { get; }

    /// <summary>All materials (recipe_id, item_id, count) required by one recipe.</summary>
    IEnumerable<RecipeMaterialRow> GetRecipeMaterials(int recipeId);

    /// <summary>Fish ids available in a pond, in pond.fish_pool order.</summary>
    IReadOnlyList<int> GetPondFishPool(int pondId);
}

/// <summary>
/// Fallback implementation: everything empty. Present ONLY until Luban codegen
/// has run at least once (see header comment).
/// </summary>
public sealed class EmptyTableService : ITableService
{
    public static readonly EmptyTableService Instance = new();

    public IReadOnlyDictionary<int, ItemRow> Items { get; } = new Dictionary<int, ItemRow>();
    public IReadOnlyDictionary<int, FishRow> Fish { get; } = new Dictionary<int, FishRow>();
    public IReadOnlyDictionary<int, BaitRow> Baits { get; } = new Dictionary<int, BaitRow>();
    public IReadOnlyDictionary<int, RodRow> Rods { get; } = new Dictionary<int, RodRow>();
    public IReadOnlyDictionary<int, PondRow> Ponds { get; } = new Dictionary<int, PondRow>();
    public IReadOnlyDictionary<int, RecipeRow> Recipes { get; } = new Dictionary<int, RecipeRow>();
    public IReadOnlyList<RecipeMaterialRow> RecipeMaterials { get; } = Array.Empty<RecipeMaterialRow>();
    public IReadOnlyDictionary<int, SeedRow> Seeds { get; } = new Dictionary<int, SeedRow>();
    public IReadOnlyDictionary<int, ShopRow> Shop { get; } = new Dictionary<int, ShopRow>();
    public IReadOnlyList<TaskRow> Tasks { get; } = Array.Empty<TaskRow>();
    public IReadOnlyList<LevelExpRow> LevelExp { get; } = Array.Empty<LevelExpRow>();

    public IEnumerable<RecipeMaterialRow> GetRecipeMaterials(int recipeId) =>
        Enumerable.Empty<RecipeMaterialRow>();

    public IReadOnlyList<int> GetPondFishPool(int pondId) => Array.Empty<int>();
}
