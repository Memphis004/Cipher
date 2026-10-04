using UnityEngine;

namespace ProjectSpy.Unity.Tactical
{
    /// <summary>
    /// Destroys a Unity object correctly whether or not the game is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Object.Destroy</c> is illegal outside play mode: Unity logs an error, ignores the
    /// call, and leaves the object where it is. That is not a crash and it is not visible
    /// in a screenshot — it is a collider that stays on a proxy quad, or a stale light that
    /// is still in the scene, and the code reads as though it worked.
    /// </para>
    /// <para>
    /// The tactical view builds and rebuilds objects at edit time as well as in play —
    /// the scene generator previews, the EditMode tests drive <c>LightingDirector</c> for
    /// real — so it cannot assume a running game loop.
    /// </para>
    /// </remarks>
    public static class TacticalObject
    {
        /// <summary>Destroys an object, immediately when there is no frame to destroy it on.</summary>
        public static void Destroy(Object target)
        {
            if (target == null)
                return;

            if (Application.isPlaying)
                Object.Destroy(target);
            else
                Object.DestroyImmediate(target);
        }
    }
}