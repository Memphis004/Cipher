// Full-stack chain E2E: seed node (31236) + hub (5170) must be running and
// NetworkSettings pointed at the seed. Flow — create_avatar → SHOP UI buy →
// FISH (chain) → SELL → UNLOCK KITCHEN → CRAFT → EAT → TASKBOARD (reroll +
// submit). Every step asserts the CONFIRMED StateWatcher snapshot deltas;
// UI asserts verify the windows show the real on-chain rows
// (presenter-driven rebuild). StateWatcher polls every 500ms — the harness
// POLLS predicates instead of sleeping fixed delays.
using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using ProjectSpy.Infrastructure;
using ProjectSpy.Infrastructure.Blockchain;
using ProjectSpy.Infrastructure.Interaction;
using ProjectSpy.Infrastructure.Scene;
using ProjectSpy.Infrastructure.UI;
using ProjectSpy.Lib.Actions;
using UnityEngine;

public static class Script
{
    public static void Main()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("[ce2e] NOT PLAYING - abort");
            return;
        }
        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        try
        {
            await TestAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ce2e] FAILED - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static async UniTask TestAsync()
    {
        UniTaskScheduler.UnobservedTaskException += ex =>
            Debug.LogError($"[ce2e] UNOBSERVED: {ex.GetType().Name}: {ex.Message}");

        Debug.Log("[ce2e] === Chain E2E start ===");

        RootLifetimeScope root = UnityEngine.Object.FindObjectOfType<RootLifetimeScope>(true);
        var chain = (ILibplanetClient)root.Container.Resolve(typeof(ILibplanetClient));
        var actions = (ActionQueue)root.Container.Resolve(typeof(ActionQueue));
        var watcher = (StateWatcher)root.Container.Resolve(typeof(StateWatcher));
        var players = (ProjectSpy.Presentation.Common.ScenePlayerService)root.Container.Resolve(
            typeof(ProjectSpy.Presentation.Common.ScenePlayerService));
        var windows = (IWindowService)root.Container.Resolve(typeof(IWindowService));
        var router = (SceneRouter)root.Container.Resolve(typeof(SceneRouter));
        var tables = (ProjectSpy.Infrastructure.DataTables.UnityTableService)root.Container.Resolve(
            typeof(ProjectSpy.Infrastructure.DataTables.UnityTableService));

        Debug.Log($"[ce2e] chain status={chain.Status} tip={chain.TipIndex} peers={chain.PeerCount}");
        if (chain.PeerCount == 0)
        {
            Debug.LogError("[ce2e] NOT connected to seed (peers=0) — check NetworkSettings");
            return;
        }

        for (int i = 0; i < 60 && !players.HasPlayer; i++)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(0.5f));
        }
        if (!players.HasPlayer)
        {
            Debug.LogError("[ce2e] player never spawned");
            return;
        }

        await Delay2PollsAsync();

        // ---- Phase 0: create_avatar (skip when it already exists) ----
        AvatarSnapshot? snap = watcher.Current;
        if (string.IsNullOrEmpty(snap?.Name))
        {
            Debug.Log("[ce2e] Phase 0: create_avatar");
            (bool ok0, string why0) = await actions.SubmitWithReasonAsync(new CreateAvatarAction("E2E"));
            if (!ok0)
            {
                Debug.LogError($"[ce2e] create_avatar failed: {why0}");
                return;
            }
            await WaitSnapshotAsync(watcher, s => !string.IsNullOrEmpty(s.Name));
            snap = watcher.Current;
            Debug.Log($"[ce2e] AVATAR OK name={snap!.Name} gold={snap.Gold} stamina={snap.Stamina}");
        }
        else
        {
            Debug.Log($"[ce2e] avatar exists name={snap!.Name} gold={snap.Gold}");
        }

        // ---- Phase 1: BUY via the real Shop UI ----
        Debug.Log("[ce2e] Phase 1: Shop UI buy bait x3 (shop entry 1 → item 1001)");
        await router.GoToAsync(SceneId.Shop, 0f, 1f);
        await UniTask.Delay(TimeSpan.FromSeconds(1.0f));
        players.Teleport(new Vector3(0f, 1.0f, 0f));

        await OpenViaDriverAsync(windows.IsOpen<ProjectSpy.Presentation.Shop.ShopWindow>,
            InteractionKind.Shopkeeper);
        if (!windows.IsOpen<ProjectSpy.Presentation.Shop.ShopWindow>())
        {
            Debug.LogError("[ce2e] ShopWindow did not open");
            return;
        }

        var shop = UnityEngine.Object.FindObjectOfType<ProjectSpy.Presentation.Shop.ShopWindow>(true);
        int rows = shop.RowList.childCount - 1;
        Debug.Log($"[ce2e] shop buy rows={rows} (expect 10 — salt row added)");
        if (rows != 10)
        {
            Debug.LogError("[ce2e] buy row count mismatch - FAIL");
            return;
        }

        long goldBefore = watcher.Current!.Gold;
        long baitBefore = watcher.Current.GetItemCount(1001);
        (bool okB, string whyB) = await actions.SubmitWithReasonAsync(new BuyItemAction(1, 3));
        if (!okB)
        {
            Debug.LogError($"[ce2e] buy failed: {whyB}");
            return;
        }
        bool buySettled = await WaitSnapshotAsync(watcher, s =>
            s.Gold == goldBefore - 15 && s.GetItemCount(1001) == baitBefore + 3);
        long goldAfterBuy = watcher.Current!.Gold;
        long baitAfter = watcher.Current.GetItemCount(1001);
        Debug.Log($"[ce2e] BUY OK gold {goldBefore}->{goldAfterBuy} (-15) bait x{baitAfter} (+3)");
        if (!buySettled)
        {
            Debug.LogError("[ce2e] buy delta mismatch - FAIL");
            return;
        }
        await windows.CloseAsync<ProjectSpy.Presentation.Shop.ShopWindow>();
        await UniTask.Delay(TimeSpan.FromSeconds(0.3f));

        // ---- Phase 2: FISH until >= 1 fish (chain RNG), then SELL it ----
        Debug.Log("[ce2e] Phase 2: occupy pond + fish");
        (bool okO, string whyO) = await actions.SubmitWithReasonAsync(new OccupyPondAction(1));
        if (!okO)
        {
            Debug.LogError($"[ce2e] occupy failed: {whyO}");
            return;
        }
        await Delay2PollsAsync();

        long fishBefore = watcher.Current!.GetItemCount(3001);
        int caught = 0;
        // Grilled fish (recipe 1) needs fish 3001 SPECIFICALLY, but the pond
        // pool is weighted (3001 ≈ 31% of hits — rest is bycatch 3002-3004),
        // and the sell check must not eat the craft material. Exit when we
        // hold a 3001 AND something else to sell (or 2× 3001).
        for (int attempt = 1; attempt <= 60; attempt++)
        {
            // Bait top-up: refill at ≤2 instead of failing mid-run.
            if (watcher.Current!.GetItemCount(1001) <= 2)
            {
                (bool okTop, string whyTop) = await actions.SubmitWithReasonAsync(
                    new BuyItemAction(1, 10));
                if (!okTop)
                {
                    Debug.LogError($"[ce2e] bait top-up failed: {whyTop}");
                    return;
                }
                await WaitSnapshotAsync(watcher, s => s.GetItemCount(1001) >= 10);
            }

            long expBefore = watcher.Current!.FishingExp;
            (bool okF, string whyF) = await actions.SubmitWithReasonAsync(new FishingAction(1, 1001));
            if (!okF)
            {
                Debug.LogError($"[ce2e] fishing attempt {attempt} failed: {whyF}");
                return;
            }
            // Exp moves on EVERY cast (1 on miss, reward on hit) — the true
            // per-cast confirmation; counting only 3001 hides bycatch.
            await WaitSnapshotAsync(watcher, s => s.FishingExp > expBefore, 6000);
            caught = (int)(watcher.Current!.GetItemCount(3001) - fishBefore);
            long bycatch = watcher.Current!.GetItemCount(3002)
                + watcher.Current!.GetItemCount(3003)
                + watcher.Current!.GetItemCount(3004);
            if (caught >= 2 || (caught >= 1 && bycatch >= 1))
            {
                break;
            }
        }

        long c3001 = watcher.Current!.GetItemCount(3001);
        long bycatchNow = watcher.Current!.GetItemCount(3002)
            + watcher.Current!.GetItemCount(3003)
            + watcher.Current!.GetItemCount(3004);
        Debug.Log($"[ce2e] FISH OK caught3001={caught} fish3001 x{c3001} bycatch x{bycatchNow} exp={watcher.Current.FishingExp}");
        if (c3001 < 1 || (c3001 < 2 && bycatchNow < 1))
        {
            Debug.LogError("[ce2e] fish stock insufficient after 60 casts — rerun");
            return;
        }

        // Sell tab UI check + sell one fish.
        await router.GoToAsync(SceneId.Shop, 0f, 1f);
        await UniTask.Delay(TimeSpan.FromSeconds(1.0f));
        await OpenViaDriverAsync(windows.IsOpen<ProjectSpy.Presentation.Shop.ShopWindow>,
            InteractionKind.Shopkeeper);
        // Sell a fish — prefer bycatch so the 3001 survives for the craft.
        AvatarSnapshot cur = watcher.Current!;
        int sellItemId = cur.GetItemCount(3002) >= 1 ? 3002
            : cur.GetItemCount(3003) >= 1 ? 3003
            : cur.GetItemCount(3004) >= 1 ? 3004
            : 3001; // only reachable when 2× 3001 are held
        long sellBefore = cur.GetItemCount(sellItemId);
        long goldBeforeSell = cur.Gold;
        (bool okS, string whyS) = await actions.SubmitWithReasonAsync(new SellItemAction(sellItemId, 1));
        if (!okS)
        {
            Debug.LogError($"[ce2e] sell failed: {whyS}");
            return;
        }
        bool sellSettled = await WaitSnapshotAsync(watcher, s =>
            s.GetItemCount(sellItemId) == sellBefore - 1 && s.Gold > goldBeforeSell);
        long goldAfterSell = watcher.Current!.Gold;
        long fishAfterSell = watcher.Current.GetItemCount(sellItemId);
        Debug.Log($"[ce2e] SELL OK item={sellItemId} x{fishAfterSell} gold={goldAfterSell}");
        if (!sellSettled)
        {
            Debug.LogError("[ce2e] sell sanity check failed");
            return;
        }
        await windows.CloseAsync<ProjectSpy.Presentation.Shop.ShopWindow>();
        await UniTask.Delay(TimeSpan.FromSeconds(0.3f));

        // ---- Phase 3: UNLOCK KITCHEN (new unlock_kitchen_v1) ----
        // Chain state persists across reruns — a prior session may have
        // unlocked already (the action's one-time guard throws then).
        if (watcher.Current!.KitchenUnlocked)
        {
            Debug.Log("[ce2e] Phase 3: kitchen already unlocked (prior run) — skip tx");
        }
        else
        {
        Debug.Log("[ce2e] Phase 3: unlock kitchen (-50 gold)");
        long goldBeforeUnlock = watcher.Current!.Gold;
        (bool okU, string whyU) = await actions.SubmitWithReasonAsync(
            new ProjectSpy.Lib.Actions.UnlockKitchenAction());
        if (!okU)
        {
            Debug.LogError($"[ce2e] unlock failed: {whyU}");
            return;
        }
        bool unlockSettled = await WaitSnapshotAsync(watcher, s =>
            s.KitchenUnlocked && s.Gold == goldBeforeUnlock - 50);
        Debug.Log($"[ce2e] UNLOCK OK kitchen={watcher.Current!.KitchenUnlocked} gold={watcher.Current.Gold}");
        if (!unlockSettled)
        {
            Debug.LogError("[ce2e] unlock delta mismatch - FAIL");
            return;
        }
        }

        // ---- Phase 4: CRAFT via the real Craft UI ----
        Debug.Log("[ce2e] Phase 4: Craft UI grilled fish (needs 3001 + 6001)");
        await router.GoToAsync(SceneId.AuntieHouse, 0f, 0f);
        await UniTask.Delay(TimeSpan.FromSeconds(1.0f));
        await OpenViaDriverAsync(windows.IsOpen<ProjectSpy.Presentation.AuntieHouse.CraftWindow>,
            InteractionKind.Kitchen);
        if (!windows.IsOpen<ProjectSpy.Presentation.AuntieHouse.CraftWindow>())
        {
            Debug.LogError("[ce2e] CraftWindow did not open");
            return;
        }

        var craft = UnityEngine.Object.FindObjectOfType<ProjectSpy.Presentation.AuntieHouse.CraftWindow>(true);
        Debug.Log($"[ce2e] craft rows={craft.RowList.childCount - 1} lockedOverlay={craft.LockedOverlay.activeSelf} unlockBtn={(craft.UnlockButton is { } ? "ok" : "MISSING")}");
        if (craft.LockedOverlay.activeSelf)
        {
            Debug.LogError("[ce2e] kitchen still locked after unlock - FAIL");
            return;
        }

        // Materials: buy salt (6001) from the shop table — crafted item needs
        // 1 fish + 1 salt. Fish is already owned from Phase 2.
        long goldBeforeSalt = watcher.Current!.Gold;
        long saltBefore = watcher.Current!.GetItemCount(6001);
        (bool okSalt, string whySalt) = await actions.SubmitWithReasonAsync(new BuyItemAction(10, 1));
        if (!okSalt)
        {
            Debug.LogError($"[ce2e] salt buy failed (shop entry 10): {whySalt}");
            return;
        }
        bool saltSettled = await WaitSnapshotAsync(watcher, s =>
            s.GetItemCount(6001) == saltBefore + 1 && s.Gold == goldBeforeSalt - 3);
        if (!saltSettled)
        {
            Debug.LogError("[ce2e] salt buy delta mismatch - FAIL");
            return;
        }

        long foodBefore = watcher.Current!.GetItemCount(7001);
        long greatBefore = watcher.Current.GetItemCount(7002);
        (bool okC, string whyC) = await actions.SubmitWithReasonAsync(new CraftFoodAction(1, 1));
        if (!okC)
        {
            Debug.LogError($"[ce2e] craft failed: {whyC}");
            return;
        }
        bool craftSettled = await WaitSnapshotAsync(watcher, s =>
            s.GetItemCount(7001) > foodBefore || s.GetItemCount(7002) > greatBefore);
        long foodAfter = watcher.Current!.GetItemCount(7001);
        long greatAfter = watcher.Current.GetItemCount(7002);
        Debug.Log($"[ce2e] CRAFT OK food7001 {foodBefore}->{foodAfter} great7002 {greatBefore}->{greatAfter}");
        if (!craftSettled)
        {
            Debug.LogError("[ce2e] no food produced - FAIL");
            return;
        }
        await windows.CloseAsync<ProjectSpy.Presentation.AuntieHouse.CraftWindow>();

        // ---- Phase 5: EAT (eat_food_v1) — consume the crafted dish ----
        Debug.Log("[ce2e] Phase 5: eat grilled fish (stamina +15)");
        if (watcher.Current!.GetItemCount(7001) < 1)
        {
            // Rerun safety: a prior run already ate the dish — craft one more
            // (sources fish + salt first via the same path as task materials).
            Debug.Log("[ce2e] Phase 5: no 7001 left — crafting another");
            bool crafted = await EnsureTaskMaterialsAsync(actions, watcher, tables, 7001, 1);
            if (!crafted)
            {
                Debug.LogError("[ce2e] could not obtain a grilled fish to eat — FAIL");
                return;
            }
        }

        long staminaBeforeEat = watcher.Current!.Stamina;
        long foodBeforeEat = watcher.Current!.GetItemCount(7001);
        (bool okE, string whyE) = await actions.SubmitWithReasonAsync(new EatFoodAction(7001));
        if (!okE)
        {
            Debug.LogError($"[ce2e] eat failed: {whyE}");
            return;
        }
        // Stamina is ALSO block-synced (1/block since the last stamp), so the
        // exact +15 delta can be polluted by regen ticks — assert food −1 and
        // stamina not lower (restore is ≥ 0, capped at max).
        bool eatSettled = await WaitSnapshotAsync(watcher, s =>
            s.GetItemCount(7001) == foodBeforeEat - 1 && s.Stamina >= staminaBeforeEat);
        Debug.Log($"[ce2e] EAT OK food7001 x{watcher.Current!.GetItemCount(7001)} stamina {staminaBeforeEat}->{watcher.Current.Stamina}");
        if (!eatSettled)
        {
            Debug.LogError("[ce2e] eat delta mismatch - FAIL");
            return;
        }

        // ---- Phase 6: TASKBOARD (reroll_taskboard_v1 + submit_task_v1) via
        // the real TaskBoard UI ----
        Debug.Log("[ce2e] Phase 6: taskboard reroll + submit");
        await router.GoToAsync(SceneId.Village, 0f, 1f);
        await UniTask.Delay(TimeSpan.FromSeconds(1.0f));
        players.Teleport(new Vector3(3.0f, 2.0f, 0f));
        await OpenViaDriverAsync(windows.IsOpen<ProjectSpy.Presentation.Village.TaskBoardWindow>,
            InteractionKind.TaskBoard);
        if (!windows.IsOpen<ProjectSpy.Presentation.Village.TaskBoardWindow>())
        {
            Debug.LogError("[ce2e] TaskBoardWindow did not open");
            return;
        }

        var board = UnityEngine.Object.FindObjectOfType<ProjectSpy.Presentation.Village.TaskBoardWindow>(true);
        Debug.Log($"[ce2e] task rows={board.RowList.childCount - 1}");
        await windows.CloseAsync<ProjectSpy.Presentation.Village.TaskBoardWindow>();
        await UniTask.Delay(TimeSpan.FromSeconds(0.3f));

        // Fresh board when empty or the reroll period (600 blocks) elapsed.
        if (watcher.Current!.Tasks.Count == 0 ||
            watcher.Current!.BlockIndex - watcher.Current!.TasksLastRerolledAt >= 600)
        {
            Debug.Log("[ce2e] Phase 6: reroll_taskboard_v1");
            (bool okR, string whyR) = await actions.SubmitWithReasonAsync(
                new RerollTaskBoardAction());
            if (!okR)
            {
                Debug.LogError($"[ce2e] reroll failed: {whyR}");
                return;
            }
            bool rerollSettled = await WaitSnapshotAsync(watcher, s =>
                s.Tasks.Count == 3 && s.BlockIndex - s.TasksLastRerolledAt < 600);
            if (!rerollSettled)
            {
                Debug.LogError("[ce2e] board did not refresh after reroll - FAIL");
                return;
            }
        }

        var boardTasks = watcher.Current!.Tasks;
        Debug.Log($"[ce2e] board: {string.Join(",", boardTasks.Keys)} done: {string.Join(",", boardTasks.Where(kv => kv.Value).Select(kv => kv.Key))}");
        if (boardTasks.Count < 3)
        {
            Debug.LogError("[ce2e] expected 3 tasks on the board - FAIL");
            return;
        }

        // Pick an incomplete task whose materials we can obtain.
        int chosen = 0;
        int needItem = 0;
        int needCount = 0;
        int rewardGold = 0;
        foreach (var kv in boardTasks)
        {
            if (kv.Value)
            {
                continue;
            }

            var row = tables.Tables.TbTask.DataList.FirstOrDefault(r => r.Id == kv.Key);
            if (row is null)
            {
                continue;
            }

            if (chosen == 0 || watcher.Current!.GetItemCount(row.TargetItemId) >= row.TargetCount)
            {
                chosen = kv.Key;
                needItem = row.TargetItemId;
                needCount = row.TargetCount;
                rewardGold = row.RewardGold;
                if (watcher.Current!.GetItemCount(needItem) >= needCount)
                {
                    break; // immediately satisfiable — best pick
                }
            }
        }

        if (chosen == 0)
        {
            Debug.Log("[ce2e] TASK SKIP: all 3 board tasks already completed (prior run)");
        }
        else
        {
            Debug.Log($"[ce2e] Phase 6: submit task {chosen} (needs {needCount}x {needItem}, +{rewardGold}g)");
            bool stocked = await EnsureTaskMaterialsAsync(actions, watcher, tables, needItem, needCount);
            if (!stocked)
            {
                Debug.LogError($"[ce2e] cannot obtain {needCount}x item {needItem} for task {chosen} — FAIL");
                return;
            }

            long goldBeforeTask = watcher.Current!.Gold;
            long itemBeforeTask = watcher.Current!.GetItemCount(needItem);
            (bool okT, string whyT) = await actions.SubmitWithReasonAsync(new SubmitTaskAction(chosen));
            if (!okT)
            {
                Debug.LogError($"[ce2e] task submit failed: {whyT}");
                return;
            }
            bool taskSettled = await WaitSnapshotAsync(watcher, s =>
                s.Tasks.TryGetValue(chosen, out bool done) && done &&
                s.GetItemCount(needItem) == itemBeforeTask - needCount &&
                s.Gold == goldBeforeTask + rewardGold);
            Debug.Log($"[ce2e] TASK OK id={chosen} gold {goldBeforeTask}->{watcher.Current!.Gold} (+{rewardGold})");
            if (!taskSettled)
            {
                Debug.LogError("[ce2e] task reward delta mismatch - FAIL");
                return;
            }
        }

        Debug.Log($"[ce2e] === Chain E2E ALL PASS === tip={chain.TipIndex} peers={chain.PeerCount}");
    }

    /// <summary>Ensures <paramref name="count"/> of <paramref name="itemId"/>
    /// is held: top-up via the item's shop entry, or via <paramref
    /// name="produce"/> (e.g. craft) when the shop does not sell it.
    /// Polls the confirmed snapshot after every tx. False on failure.</summary>
    private static async UniTask<bool> TopUpItemAsync(
        ActionQueue actions,
        StateWatcher watcher,
        int itemId,
        int count,
        Func<ProjectSpy.Lib.Actions.ActionBase>? produce,
        ProjectSpy.Infrastructure.DataTables.UnityTableService tables)
    {
        int guard = 0;
        while (watcher.Current!.GetItemCount(itemId) < count)
        {
            if (++guard > 20)
            {
                return false;
            }

            int entry = ShopEntryIdForItem(tables, itemId);
            if (entry > 0)
            {
                (bool ok, string why) = await actions.SubmitWithReasonAsync(new BuyItemAction(entry, 10));
                if (!ok)
                {
                    Debug.LogError($"[ce2e] top-up buy entry {entry} failed: {why}");
                    return false;
                }
                long before = watcher.Current!.GetItemCount(itemId);
                await WaitSnapshotAsync(watcher, s => s.GetItemCount(itemId) > before);
            }
            else if (produce is { })
            {
                (bool ok, string why) = await actions.SubmitWithReasonAsync(produce());
                if (!ok)
                {
                    Debug.LogError($"[ce2e] top-up produce failed: {why}");
                    return false;
                }
                long before = watcher.Current!.GetItemCount(itemId);
                await WaitSnapshotAsync(watcher, s => s.GetItemCount(itemId) > before);
            }
            else
            {
                return false; // not sold anywhere and no producer given
            }
        }

        return true;
    }

    /// <summary>Mirrors ShopPresenter.ShopEntryIdFor — the shop table maps
    /// ENTRY ids to ITEM ids (buy_item_v1 takes the entry id).</summary>
    private static int ShopEntryIdForItem(
        ProjectSpy.Infrastructure.DataTables.UnityTableService tables, int itemId)
    {
        foreach (var row in tables.Tables.TbShop.DataList)
        {
            if (row.ItemId == itemId)
            {
                return row.Id;
            }
        }

        return 0;
    }

    /// <summary>Sources task materials by category: fish via real casts (the
    /// pond pool is random, so this loops until the SPECIFIC fish is held),
    /// grilled food via recipe 1 (fish + salt, both sourced first),
    /// everything else via the shop.</summary>
    private static async UniTask<bool> EnsureTaskMaterialsAsync(
        ActionQueue actions,
        StateWatcher watcher,
        ProjectSpy.Infrastructure.DataTables.UnityTableService tables,
        int itemId,
        int count)
    {
        if (itemId is >= 3001 and <= 3009)
        {
            return await FishUpAsync(actions, watcher, itemId, count);
        }

        if (itemId is >= 7001 and <= 7009)
        {
            // Recipe 1 (grilled fish): 1x fish 3001 + 1x salt 6001 per portion.
            while (watcher.Current!.GetItemCount(itemId) < count)
            {
                if (!await FishUpAsync(actions, watcher, 3001, 1))
                {
                    return false;
                }

                if (!await TopUpItemAsync(actions, watcher, 6001, 1, null, tables))
                {
                    return false;
                }

                (bool ok, string why) = await actions.SubmitWithReasonAsync(new CraftFoodAction(1, 1));
                if (!ok)
                {
                    Debug.LogError($"[ce2e] craft for task failed: {why}");
                    return false;
                }

                long before = watcher.Current!.GetItemCount(itemId);
                await WaitSnapshotAsync(watcher, s => s.GetItemCount(itemId) > before);
            }

            return true;
        }

        // Everything else: the shop (or nothing).
        return await TopUpItemAsync(actions, watcher, itemId, count, null, tables);
    }

    /// <summary>Fishes until <paramref name="count"/> of the SPECIFIC fish is
    /// held: (re-)occupies the pond (renew is idempotent, expired leases are
    /// re-granted), tops up bait, casts up to 40 times (pool is weighted —
    /// the target fish may take a while).</summary>
    private static async UniTask<bool> FishUpAsync(
        ActionQueue actions, StateWatcher watcher, int fishItemId, int count)
    {
        (bool okO, string whyO) = await actions.SubmitWithReasonAsync(new OccupyPondAction(1));
        if (!okO)
        {
            Debug.LogError($"[ce2e] task re-occupy failed: {whyO}");
            return false;
        }
        await Delay2PollsAsync();

        for (int attempt = 0;
            attempt < 40 && watcher.Current!.GetItemCount(fishItemId) < count;
            attempt++)
        {
            if (watcher.Current!.GetItemCount(1001) <= 2)
            {
                (bool okTop, string whyTop) = await actions.SubmitWithReasonAsync(
                    new BuyItemAction(1, 10));
                if (!okTop)
                {
                    Debug.LogError($"[ce2e] bait top-up failed: {whyTop}");
                    return false;
                }
                await WaitSnapshotAsync(watcher, s => s.GetItemCount(1001) >= 10);
            }

            long expBefore = watcher.Current!.FishingExp;
            (bool okF, string whyF) = await actions.SubmitWithReasonAsync(new FishingAction(1, 1001));
            if (!okF)
            {
                Debug.LogError($"[ce2e] task fishing failed: {whyF}");
                return false;
            }
            await WaitSnapshotAsync(watcher, s => s.FishingExp > expBefore, 6000);
        }

        return watcher.Current!.GetItemCount(fishItemId) >= count;
    }

    // ---- helpers ----------------------------------------------------------

    private static async UniTask Delay2PollsAsync() =>
        await UniTask.Delay(TimeSpan.FromSeconds(1.1f));

    /// <summary>Waits until the watcher's confirmed snapshot satisfies
    /// <paramref name="condition"/> (StateWatcher polls every 500ms but blocks
    /// can arrive later than a fixed 2-poll wait — the harness must poll).
    /// Returns false on timeout.</summary>
    private static async UniTask<bool> WaitSnapshotAsync(
        StateWatcher watcher, Func<AvatarSnapshot, bool> condition, int timeoutMs = 15000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 250)
        {
            if (watcher.Current is { } s && condition(s))
            {
                return true;
            }
            await UniTask.Delay(250);
        }
        return false;
    }

    /// <summary>Opens a window through the driver's OpenAsync (the same code
    /// path as a real [E] press) and waits for <paramref name="opened"/>.</summary>
    private static async UniTask OpenViaDriverAsync(
        Func<bool> opened, InteractionKind kind)
    {
        var driver = UnityEngine.Object.FindObjectOfType<InteractionPromptDriver>();
        if (driver is null)
        {
            throw new InvalidOperationException("no InteractionPromptDriver");
        }

        var method = typeof(InteractionPromptDriver).GetMethod(
            "OpenAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(driver, new object[] { kind });

        for (int i = 0; i < 240 && !opened(); i++)
        {
            await UniTask.Yield();
        }
    }
}
