using System;
using Cysharp.Threading.Tasks;
using ProjectSpy.Infrastructure;
using UnityEngine;

public static class Script
{
    public static void Main()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("[e2e-pos] NOT PLAYING");
            return;
        }

        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        try
        {
            RootLifetimeScope root = UnityEngine.Object.FindObjectOfType<RootLifetimeScope>(true);
            var players = (ProjectSpy.Presentation.Common.ScenePlayerService)root.Container.Resolve(
                typeof(ProjectSpy.Presentation.Common.ScenePlayerService));

            for (int i = 0; i < 60 && !players.HasPlayer; i++)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(0.5f));
            }

            players.Teleport(new Vector3(3.0f, 2.0f, 0f));
            await UniTask.Delay(TimeSpan.FromSeconds(0.6f));

            Vector3 pos = players.Player.transform.position;
            var interactables = UnityEngine.Object.FindObjectsOfType<ProjectSpy.Infrastructure.Interaction.TaskBoardInteractable>(true);
            var detector = UnityEngine.Object.FindObjectOfType<ProjectSpy.Infrastructure.Interaction.InteractionDetector>();

            Debug.Log($"[e2e-pos] player=({pos.x:0.0},{pos.y:0.0}) boardInteractables={interactables.Length}");
            foreach (var t in interactables)
            {
                Debug.Log($"[e2e-pos] board at '{t.transform.position}' promptKey='{t.PromptKey}' canInteract={t.CanInteract}");
            }

            if (detector is { })
            {
                Debug.Log($"[e2e-pos] detector present, current={(detector.Current is null ? "<none>" : detector.Current.GetType().Name)}");
            }
            else
            {
                Debug.Log("[e2e-pos] detector MISSING on player");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[e2e-pos] FAILED - {ex.GetType().Name}: {ex.Message}");
        }
    }
}
