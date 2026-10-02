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
            Debug.Log("[e2e-det] NOT PLAYING");
            return;
        }

        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        try
        {
            ProjectSpy.Infrastructure.Interaction.TaskBoardInteractable board =
                UnityEngine.Object.FindObjectOfType<ProjectSpy.Infrastructure.Interaction.TaskBoardInteractable>(true);
            Debug.Log($"[e2e-det] includeInactive find: {(board is null ? "NULL" : "found")}");

            // Dump ALL components on the TaskBoard clone at runtime.
            GameObject boardGo = GameObject.Find("TaskBoard(Clone)");
            if (boardGo is null)
            {
                Debug.Log("[e2e-det] GameObject 'TaskBoard(Clone)' not found by name");
                return;
            }

            Debug.Log($"[e2e-det] board '{boardGo.name}' active={boardGo.activeInHierarchy}");
            foreach (Component c in boardGo.GetComponents<Component>())
            {
                Debug.Log($"[e2e-det]   component: {c.GetType().FullName} enabled={(c is Behaviour b ? b.enabled.ToString() : "-")}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[e2e-det] FAILED - {ex.GetType().Name}: {ex.Message}");
        }
    }
}
