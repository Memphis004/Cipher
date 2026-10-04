namespace ProjectSpy.Unity.Services
{
    /// <summary>
    /// Marker for anything the root lifetime scope owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists so a shutdown pass can dispose every service in one place, and so a service
    /// can be told apart from a plain helper that happens to be constructed at boot.
    /// </para>
    /// <para>
    /// In its own namespace rather than inside <c>Boot</c> so that services do not have to
    /// reference the boot namespace to declare what they are. The dependency runs the right
    /// way: Boot knows about the services, not the reverse.
    /// </para>
    /// </remarks>
    public interface IProjectSpyService
    {
    }
}