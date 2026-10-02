using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using ProjectSpy.Infrastructure;
using ProjectSpy.Infrastructure.Interaction;
using ProjectSpy.Infrastructure.Scene;
using ProjectSpy.Infrastructure.UI;
using UnityEngine;

// T3: hop -> AuntieHouse, teleport next to the kitchen counter (2,-2),
// verify detector + prompt, open CraftWindow through the driver's OpenAsync
// (same code path as the real [E] press), assert + close.
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

        Debug.Log("[e2e] === T3 Kitchen (-> AuntieHouse) start ===");

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
            Debug.LogError("[e2e] T3 player never spawned");
            return;
        }

        // --- Hop to AuntieHouse ---
        await router.GoToAsync(SceneId.AuntieHouse, 0f, 0f);
        await UniTask.Delay(TimeSpan.FromSeconds(1.0f));
        Debug.Log($"[e2e] T3 hopped: router.Current={router.Current}");

        InteractionDetector detector = UnityEngine.Object.FindObjectOfType<InteractionDetector>();
        if (detector is null)
        {
            Debug.LogError("[e2e] T3 no InteractionDetector after hop");
            return;
        }

        // Kitchen counter at (2,-2) — stop one tile to its left.
        IInteractable? target = null;
        for (int i = 0; i < 6 && target is null; i++)
        {
            players.Teleport(new Vector3(1.0f, -2.0f, 0f));
            await UniTask.Delay(TimeSpan.FromSeconds(0.4f));
            target = detector.Current;
        }

        Debug.Log($"[e2e] T3 detector: current={(target is null ? "<none>" : target.GetType().Name)} prompt={(target is null ? "-" : target.Prompt)}");
        if (target is null || target is not KitchenInteractable)
        {
            Debug.LogError("[e2e] T3 detector did NOT find the KitchenInteractable - FAIL");
            return;
        }

        var driver = UnityEngine.Object.FindObjectOfType<InteractionPromptDriver>();
        Debug.Log($"[e2e] T3 driver present={(driver is { })} (expect True)");
        if (driver is null)
        {
            Debug.LogError("[e2e] T3 driver missing - FAIL");
            return;
        }

        InvokeOpenAsync(driver, target.Kind);

        bool opened = false;
        for (int i = 0; i < 480 && !opened; i++)
        {
            await UniTask.Yield();
            opened = windows.IsOpen<ProjectSpy.Presentation.AuntieHouse.CraftWindow>();
        }
        if (!opened)
        {
            Debug.LogError("[e2e] T3 CraftWindow did NOT open - FAIL");
            return;
        }

        await UniTask.Delay(TimeSpan.FromSeconds(0.4f));
        ProjectSpy.Presentation.AuntieHouse.CraftWindow craft =
            UnityEngine.Object.FindObjectOfType<ProjectSpy.Presentation.AuntieHouse.CraftWindow>(true);
        Debug.Log($"[e2e] T3 CraftWindow OPEN: count={windows.Count} rows={craft.RowList.childCount - 1}");

        await windows.CloseAsync<ProjectSpy.Presentation.AuntieHouse.CraftWindow>();
        await UniTask.NextFrame();
        Debug.Log("[e2e] === T3 Kitchen PASS ===");
    }

    private static void InvokeOpenAsync(InteractionPromptDriver driver, InteractionKind kind)
    {
        var method = typeof(InteractionPromptDriver).GetMethod(
            "OpenAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(driver, new object[] { kind });
    }
}
