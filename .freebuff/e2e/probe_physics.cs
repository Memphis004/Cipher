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
            Debug.Log("[e2e-phys] NOT PLAYING");
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

            Vector2 origin = players.Player.transform.position;
            Debug.Log($"[e2e-phys] player at {origin}");

            Collider2D[] hits = Physics2D.OverlapCircleAll(origin, 3f);
            Debug.Log($"[e2e-phys] colliders within 3f: {hits.Length}");
            foreach (Collider2D hit in hits)
            {
                Debug.Log($"[e2e-phys]   '{hit.name}' at {hit.transform.position} trigger={hit.isTrigger} enabled={hit.enabled} active={hit.gameObject.activeInHierarchy}");
            }

            ProjectSpy.Infrastructure.Interaction.TaskBoardInteractable board =
                UnityEngine.Object.FindObjectOfType<ProjectSpy.Infrastructure.Interaction.TaskBoardInteractable>(true);
            if (board is { })
            {
                BoxCollider2D col = board.GetComponent<BoxCollider2D>();
                Debug.Log($"[e2e-phys] board: pos={board.transform.position} colNull={(col is null)} trigger={(col is null ? "-" : col.isTrigger.ToString())} size={(col is null ? "-" : col.size.ToString())} active={board.gameObject.activeInHierarchy}");
                Collider2D[] boardHits = Physics2D.OverlapBoxAll(board.transform.position, new Vector2(2f, 2f), 0f);
                Debug.Log($"[e2e-phys] OverlapBox at board found {boardHits.Length} colliders");
            }
            else
            {
                Debug.Log("[e2e-phys] board interactable NOT FOUND (even inactive)");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[e2e-phys] FAILED - {ex.GetType().Name}: {ex.Message}");
        }
    }
}
