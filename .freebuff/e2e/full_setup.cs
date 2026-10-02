using UnityEditor;
using UnityEngine;

public static class Script
{
    public static void Main()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[e2e-full] play mode active - abort");
            return;
        }

        try
        {
            ProjectSpy.Editor.BatchSetup.RunFullSetupSteps();
            ProjectSpy.Editor.ValidationReport report = ProjectSpy.Editor.ProjectValidator.Validate();
            string text = report.Render();
            if (report.Ok)
            {
                Debug.Log($"[e2e-full] {text}");
            }
            else
            {
                Debug.LogError($"[e2e-full] {text}");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[e2e-full] FAILED - {ex}");
        }
    }
}
