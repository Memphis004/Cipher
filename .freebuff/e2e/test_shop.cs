using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using ProjectSpy.Infrastructure;
using ProjectSpy.Infrastructure.Interaction;
using ProjectSpy.Infrastructure.Scene;
using ProjectSpy.Infrastructure.UI;
using UnityEngine;

// T2: hop Village -> Shop via SceneRouter, teleport next to the shopkeeper
// NPC (0,2), verify detector + prompt, open ShopWindow through the driver's
// OpenAsync (same code path as the real [E] press), assert + close.
public static class Script
{
    public static void Main()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("[e2e] NOT PLAYING - abort");
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
            Debug.LogError($"[e2e] FAILED - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static async UniTask TestAsync()
    {
        UniTaskScheduler.UnobservedTaskException += ex =>
            Debug.LogError($"[e2e] UNOBSERVED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        Debug.Log("[e2e] === T2 Shop NPC (Village -> Shop) start ===");

        RootLifetimeScope root = UnityEngine.Object.FindObjectOfType<RootLifetimeScope>(true);
        var players = (ProjectSpy.Presentation.Common.ScenePlayerService)root.Container.Resolve(
            typeof(ProjectSpy.Presentation.Common.ScenePlayerService));
        var windows = (IWindowService)root.Container.Resolve(typeof(IWindowService));
        var router = (SceneRouter)root.Container.Resolve(typeof(SceneRouter));

        for (int i = 0; i < 60 && !players.HasPlayer; i++)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(0.5f));
        }
        if (!players.HasPlayer)
        {
            Debug.LogError("[e2e] T2 player never spawned");
            return;
        }

        // --- Hop to Shop ---
        await router.GoToAsync(SceneId.Shop, 0f, 0f);
        await UniTask.Delay(TimeSpan.FromSeconds(1.0f));
        Debug.Log($"[e2e] T2 hopped: router.Current={router.Current}");

        InteractionDetector detector = UnityEngine.Object.FindObjectOfType<InteractionDetector>();
        if (detector is null)
        {
            Debug.LogError("[e2e] T2 no InteractionDetector after hop");
            return;
        }

        // NPC stands at (0,2) — stop one tile below.
        IInteractable? target = null;
        for (int i = 0; i < 6 && target is null; i++)
        {
            players.Teleport(new Vector3(0f, 1.0f, 0f));
            await UniTask.Delay(TimeSpan.FromSeconds(0.4f));
            target = detector.Current;
        }

        Debug.Log($"[e2e] T2 detector: current={(target is null ? "<none>" : target.GetType().Name)} prompt={(target is null ? "-" : target.Prompt)}");
        if (target is null || target is not NpcInteractable)
        {
            Debug.LogError("[e2e] T2 detector did NOT find the NpcInteractable - FAIL");
            return;
        }

        var driver = UnityEngine.Object.FindObjectOfType<InteractionPromptDriver>();
        Debug.Log($"[e2e] T2 driver present={(driver is { })} (expect True)");
        if (driver is null)
        {
            Debug.LogError("[e2e] T2 driver missing - FAIL");
            return;
        }

        InvokeOpenAsync(driver, target.Kind);

        bool opened = false;
        for (int i = 0; i < 480 && !opened; i++)
        {
            await UniTask.Yield();
            opened = windows.IsOpen<ProjectSpy.Presentation.Shop.ShopWindow>();
        }
        if (!opened)
        {
            Debug.LogError("[e2e] T2 ShopWindow did NOT open - FAIL");
            return;
        }

        await UniTask.Delay(TimeSpan.FromSeconds(0.4f));
        ProjectSpy.Presentation.Shop.ShopWindow shop =
            UnityEngine.Object.FindObjectOfType<ProjectSpy.Presentation.Shop.ShopWindow>(true);
        Debug.Log($"[e2e] T2 ShopWindow OPEN: count={windows.Count} rows={shop.RowList.childCount - 1}");

        await windows.CloseAsync<ProjectSpy.Presentation.Shop.ShopWindow>();
        await UniTask.NextFrame();
        Debug.Log("[e2e] === T2 Shop PASS ===");
    }

    private static void InvokeOpenAsync(InteractionPromptDriver driver, InteractionKind kind)
    {
        var method = typeof(InteractionPromptDriver).GetMethod(
            "OpenAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(driver, new object[] { kind });
    }
}
