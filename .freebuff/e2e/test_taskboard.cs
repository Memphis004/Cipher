using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using ProjectSpy.Infrastructure;
using ProjectSpy.Infrastructure.Interaction;
using ProjectSpy.Infrastructure.UI;
using UnityEngine;

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
        Debug.Log("[e2e] === T1 TaskBoard (Village) start ===");

        // UniTaskVoid swallows exceptions into the scheduler — surface them.
        UniTaskScheduler.UnobservedTaskException += ex =>
            Debug.LogError($"[e2e] UNOBSERVED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        RootLifetimeScope root = UnityEngine.Object.FindObjectOfType<RootLifetimeScope>(true);
        var players = (ProjectSpy.Presentation.Common.ScenePlayerService)root.Container.Resolve(
            typeof(ProjectSpy.Presentation.Common.ScenePlayerService));
        var windows = (IWindowService)root.Container.Resolve(typeof(IWindowService));

        // Wait for THE player to exist (spawn happens after boot).
        for (int i = 0; i < 60 && !players.HasPlayer; i++)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(0.5f));
        }

        if (!players.HasPlayer)
        {
            Debug.LogError("[e2e] T1 player never spawned");
            return;
        }

        InteractionDetector detector = UnityEngine.Object.FindObjectOfType<InteractionDetector>();
        if (detector is null)
        {
            Debug.LogError("[e2e] T1 no InteractionDetector on player");
            return;
        }

        // Walk-in simulation: teleport next to the board (4, 2) and give the
        // detector a few frames; retry the teleport (it no-ops pre-spawn).
        IInteractable? target = null;
        for (int i = 0; i < 6 && target is null; i++)
        {
            players.Teleport(new Vector3(3.0f, 2.0f, 0f));
            await UniTask.Delay(TimeSpan.FromSeconds(0.4f));
            target = detector.Current;
        }

        Debug.Log($"[e2e] T1 detector: current={(target is null ? "<none>" : target.GetType().Name)} prompt={(target is null ? "-" : target.Prompt)}");

        if (target is null)
        {
            Debug.LogError("[e2e] T1 detector did NOT find the TaskBoard - FAIL");
            return;
        }

        var driver = UnityEngine.Object.FindObjectOfType<InteractionPromptDriver>();
        Debug.Log($"[e2e] T1 driver present={(driver is { })} (expect True)");
        if (driver is null)
        {
            Debug.LogError("[e2e] T1 driver missing - FAIL");
            return;
        }

        // E-press code path: the driver's OpenAsync (reflection — same method
        // the real keypress invokes). UniTaskVoid is fire-and-forget: invoke,
        // then poll with a plain frame-count loop (no combinator surprises).
        InvokeOpenAsync(driver, target.Kind);

        bool opened = false;
        for (int i = 0; i < 480 && !opened; i++)
        {
            await UniTask.Yield();
            opened = windows.IsOpen<ProjectSpy.Presentation.Village.TaskBoardWindow>();
        }

        if (!opened)
        {
            Debug.LogError($"[e2e] T1 TaskBoardWindow did NOT open via driver (count={windows.Count}) - trying direct open to surface the real error");
            try
            {
                await windows.OpenAsync<ProjectSpy.Presentation.Village.TaskBoardWindow,
                    ProjectSpy.Infrastructure.UI.EmptyWindowParam, ProjectSpy.Infrastructure.UI.NoWindowResult>(
                    ProjectSpy.Infrastructure.UI.EmptyWindowParam.Instance, CancellationToken.None)
                    .TimeoutWithoutException(TimeSpan.FromSeconds(3));
                Debug.Log("[e2e] T1 DIRECT open returned without exception");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[e2e] T1 DIRECT open threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            opened = windows.IsOpen<ProjectSpy.Presentation.Village.TaskBoardWindow>();
        }

        if (!opened)
        {
            Debug.LogError("[e2e] T1 TaskBoardWindow did NOT open - FAIL");
            return;
        }

        await UniTask.Delay(TimeSpan.FromSeconds(0.4f));
        ProjectSpy.Presentation.Village.TaskBoardWindow board =
            UnityEngine.Object.FindObjectOfType<ProjectSpy.Presentation.Village.TaskBoardWindow>(true);
        int rows = board.RowList.childCount - 1; // minus template
        Debug.Log($"[e2e] T1 TaskBoardWindow OPEN: count={windows.Count} rows={rows} countdown='{board.CountdownLabel.text}'");

        await windows.CloseAsync<ProjectSpy.Presentation.Village.TaskBoardWindow>();
        await UniTask.NextFrame();
        Debug.Log("[e2e] === T1 TaskBoard PASS ===");
    }

    private static void InvokeOpenAsync(InteractionPromptDriver driver, InteractionKind kind)
    {
        var method = typeof(InteractionPromptDriver).GetMethod(
            "OpenAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(driver, new object[] { kind });
    }
}
