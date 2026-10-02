using UnityEditor;
using UnityEngine;

public static class Script
{
    public static void Main()
    {
        if (!EditorApplication.isPlaying)
        {
            Debug.Log("[e2e] not playing");
            return;
        }
        EditorApplication.isPlaying = false;
        Debug.Log("[e2e] stopping play mode");
    }
}
