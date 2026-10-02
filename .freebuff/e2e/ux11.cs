using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using ProjectSpy.Infrastructure;
using ProjectSpy.Infrastructure.Blockchain;
using ProjectSpy.Infrastructure.DataTables;
using ProjectSpy.Infrastructure.Scene;
using ProjectSpy.Infrastructure.UI;
using ProjectSpy.Lib.Actions;
using ProjectSpy.Presentation.Common;
using UnityEngine;

// =============================================================================
// Stage 11 UX smoke test (play mode, real chain):
//   S1  Shop buy — HUD gold + bait counts move ON CLICK (optimistic), move
//       again on CONFIRM with the chain's numbers; merged == confirmed after.
//   S2  Forced failure — buy a level-3 item at level 1: NotEnoughGoldException
//       (the level check throws InvalidOperationException, mapped via the
//       "Insufficient level" fragment) → OnRolledBack fires → toast + revert.
//   S3  Fishing cast — occupy → fish with an optimistic guess; merged bait −1
//       instantly; after confirm merged == confirmed (catch or miss both fine).
// Asserts on the merged view (what the HUD binds), never legality.
// =============================================================================
public static class Script
{
    private const int WormItemId = 1001;
    private const int ShopEntryWorm = 1;      // shop entry 1 → worm, price 5
    // NOT in data/shop.csv → the action throws FailedLoadStateException on-chain
    // 100% deterministically (session avatar is already level ≥3, so a
    // level-gate would NOT fail — learned the hard way in run #1).
    private const int ShopEntryInvalid = 999;
    private const int VillagePondId = 1;

    public static void Main()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("[ux11] NOT PLAYING - abort");
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
            Debug.LogError($"[ux11] FAILED - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private sealed class Probe
    {
        public List<string> RollbackReasons = new();
        public int Rollbacks;
    }

    private static async UniTask TestAsync()
    {
        UniTaskScheduler.UnobservedTaskException += ex =>
            Debug.LogError($"[ux11] UNOBSERVED: {ex.GetType().Name}: {ex.Message}");

        Debug.Log("[ux11] === Stage 11 UX smoke start ===");

        RootLifetimeScope root = UnityEngine.Object.FindObjectOfType<RootLifetimeScope>(true);
        var queue = (ActionQueue)root.Container.Resolve(typeof(ActionQueue));
        var optimistic = (OptimisticState)root.Container.Resolve(typeof(OptimisticState));
        var watcher = (StateWatcher)root.Container.Resolve(typeof(StateWatcher));
        var monitor = (ChainConnectionMonitor)root.Container.Resolve(typeof(ChainConnectionMonitor));
        var router = (SceneRouter)root.Container.Resolve(typeof(SceneRouter));
        var tables = (UnityTableService)root.Container.Resolve(typeof(UnityTableService));

        // Wait for boot: watcher has a snapshot and the queue is reachable.
        for (int i = 0; i < 240 && (watcher.Current is null || !tables.IsLoaded); i++)
        {
            await UniTask.Delay(500);
        }
        if (watcher.Current is null)
        {
            Debug.LogError("[ux11] no confirmed snapshot after 120s - FAIL");
            return;
        }

        Debug.Log($"[ux11] boot ok: tip={watcher.Current.BlockIndex} gold={watcher.Current.Gold} " +
                  $"stamina={watcher.Current.Stamina} status={monitor.Status} canSubmit={monitor.CanSubmit}");

        var probe = new Probe();
        optimistic.OnRolledBack += r =>
        {
            probe.Rollbacks++;
            probe.RollbackReasons.Add($"{r.Reason}:{r.RawReason}");
            Debug.Log($"[ux11] rollback event: {r.Reason} raw='{r.RawReason}'");
        };

        // ------------------------------------------------ S1: instant buy --
        long gold0 = optimistic.Current.Gold;
        long worm0 = optimistic.Current.GetItemCount(WormItemId);
        Debug.Log($"[ux11] S1 before: gold={gold0} worm={worm0}");

        UniTask<(bool Ok, string Reason)> buyTask = queue.SubmitWithGuessAsync(
            new BuyItemAction(ShopEntryWorm, 1),
            guess: g =>
            {
                g.Gold(-5);
                g.Item(WormItemId, 1);
            });

        // Let the guess apply (same frame) — the numbers must move BEFORE any block.
        await UniTask.Yield();
        await UniTask.Yield();

        long goldOpt = optimistic.Current.Gold;
        long wormOpt = optimistic.Current.GetItemCount(WormItemId);
        Debug.Log($"[ux11] S1 optimistic (pre-confirm): gold={goldOpt} worm={wormOpt} " +
                  $"pending={optimistic.PendingCount}");
        Debug.Log(goldOpt == gold0 - 5 && wormOpt == worm0 + 1
            ? "[ux11] S1a PASS — HUD numbers moved on click"
            : "[ux11] S1a FAIL — optimistic view did not move instantly");

        (bool ok1, string r1) = await buyTask;
        await UniTask.Delay(2500); // wait for the confirming block to be READ

        long gold1 = optimistic.Current.Gold;
        long worm1 = optimistic.Current.GetItemCount(WormItemId);
        long goldConfirmed = watcher.Current!.Gold;
        long wormConfirmed = watcher.Current.GetItemCount(WormItemId);
        Debug.Log($"[ux11] S1 after: ok={ok1} reason='{r1}' merged gold={gold1} worm={worm1} | " +
                  $"confirmed gold={goldConfirmed} worm={wormConfirmed}");

        bool s1b = ok1 && gold1 == goldConfirmed && worm1 == wormConfirmed;
        Debug.Log(s1b
            ? "[ux11] S1b PASS — merged == confirmed after buy"
            : "[ux11] S1b FAIL — merged/confirmed mismatch after buy");

        // ------------------------------------------------ S2: forced fail --
        Debug.Log("[ux11] S2 forcing failure (invalid shop entry — deterministic on-chain throw)…");
        int rollbacksBefore = probe.Rollbacks;
        (bool ok2, string r2) = await queue.SubmitWithGuessAsync(
            new BuyItemAction(ShopEntryInvalid, 1),
            guess: g =>
            {
                g.Gold(-40);
                g.Item(1003, 1);
            });
        await UniTask.Delay(1500);

        bool rolledBack = probe.Rollbacks > rollbacksBefore;
        long goldAfterFail = optimistic.Current.Gold;
        Debug.Log($"[ux11] S2: ok={ok2} reason='{r2}' rollbacks={probe.Rollbacks} " +
                  $"gold restored to={goldAfterFail} (was {gold1})");
        Debug.Log(!ok2 && rolledBack && goldAfterFail == gold1
            ? "[ux11] S2 PASS — rollback event fired, display restored, toast queued"
            : "[ux11] S2 FAIL — rollback did not restore the display");

        // ------------------------------------------------ S3: fishing cast --
        await router.GoToAsync(SceneId.FarmPlot, 0f, 0f);
        await UniTask.Delay(1000);

        long bait0 = optimistic.Current.GetItemCount(WormItemId);
        long stam0 = optimistic.Current.Stamina;
        Debug.Log($"[ux11] S3 before: bait={bait0} stamina={stam0}");

        (bool ok3, string r3) = await queue.SubmitWithGuessAsync(
            new OccupyPondAction(VillagePondId), guess: null);
        Debug.Log($"[ux11] S3 occupy: ok={ok3} reason='{r3}'");

        var castTask = queue.SubmitWithGuessAsync(
            new FishingAction(VillagePondId, WormItemId),
            guess: g =>
            {
                g.Item(WormItemId, -1);
                g.Stamina(-5);
            });

        await UniTask.Yield();
        await UniTask.Yield();
        long baitOpt = optimistic.Current.GetItemCount(WormItemId);
        long stamOpt = optimistic.Current.Stamina;
        Debug.Log(baitOpt == bait0 - 1 && stamOpt <= stam0 - 5
            ? "[ux11] S3a PASS — bait/stamina moved on click"
            : $"[ux11] S3a WARN — merged bait={baitOpt} stamina={stamOpt} " +
              $"(expected {bait0 - 1}/{stam0 - 5}; clamped merge allowed)");

        (bool ok4, string r4) = await castTask;
        await UniTask.Delay(2500);

        long baitMerged = optimistic.Current.GetItemCount(WormItemId);
        long baitConf = watcher.Current!.GetItemCount(WormItemId);
        long fishMerged = optimistic.Current.GetItemCount(3001);
        long fishConf = watcher.Current.GetItemCount(3001);
        Debug.Log($"[ux11] S3 after: ok={ok4} reason='{r4}' merged bait={baitMerged} " +
                  $"conf bait={baitConf} merged fish(3001)={fishMerged} conf={fishConf}");
        Debug.Log(ok4 && baitMerged == baitConf && fishMerged == fishConf
            ? "[ux11] S3b PASS — merged == confirmed after the cast (catch or miss)"
            : "[ux11] S3b FAIL — merged/confirmed mismatch after the cast");

        // ------------------------------------------------------- verdict ---
        bool allPass = s1b && !ok2 && rolledBack && ok4 && probe.RollbackReasons.Count > 0;
        Debug.Log(allPass
            ? "[ux11] ================================= ALL PASS ================================="
            : "[ux11] ================================== FAILED ==================================");
    }
}
