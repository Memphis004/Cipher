using UnityEditor;
using UnityEngine;

public static class Script
{
    public static void Main()
    {
        if (EditorApplication.isCompiling)
        {
            Debug.Log("[e2e] still compiling - try again");
            return;
        }
        if (EditorApplication.isPlaying)
        {
            Debug.Log("[e2e] already playing");
            return;
        }
        EditorApplication.isPlaying = true;
        Debug.Log("[e2e] entering play mode");
    }
}
