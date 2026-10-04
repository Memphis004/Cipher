using ProjectSpy.Unity.Audio;
using ProjectSpy.Unity.Input;
using ProjectSpy.Unity.Localisation;
using ProjectSpy.Unity.Persistence;
using ProjectSpy.Unity.Scenes;
using ProjectSpy.Unity.Services;
using ProjectSpy.Unity.Simulation;
using ProjectSpy.Unity.UI;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace ProjectSpy.Unity.Boot
{
    /// <summary>
    /// The one lifetime scope. Everything that lives for the whole session is built here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single scope rather than one per scene, because Base and Tactical load
    /// <em>additively</em> over a persistent root (see <see cref="SceneRouter"/>). Scoped DI
    /// that only lives as long as a scene would tear down the simulation every time the
    /// player returned from a mission, which is precisely when Core's state must survive
    /// intact.
    /// </para>
    /// <para>
    /// <b>Nothing here decides anything.</b> The scope constructs services and wires their
    /// dependencies; it contains no gameplay rule and no arithmetic on a Core field. See
    /// <c>PresentationRuleValidator</c>, which enforces that this stays true.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class RootLifetimeScope : LifetimeScope, IInitializable
    {
        /// <summary>The compiled game tables.</summary>
        public TableService Tables { get; private set; }

        /// <summary>Save and load.</summary>
        public SaveService Saves { get; private set; }

        /// <summary>Additive scene loading and the persistent root.</summary>
        public SceneRouter Scenes { get; private set; }

        /// <summary>Stacked, pooled UI windows.</summary>
        public WindowService Windows { get; private set; }

        /// <summary>Transient notifications.</summary>
        public ToastService Toasts { get; private set; }

        /// <summary>Pooled audio sources.</summary>
        public AudioService Audio { get; private set; }

        /// <summary>Localized strings, Thai by default.</summary>
        public LocalizationService Localization { get; private set; }

        /// <summary>Rebindable input actions.</summary>
        public InputService Input { get; private set; }

        /// <summary>The clock driving Core.</summary>
        public SimulationRunner Simulation { get; private set; }

        /// <summary>
        /// Registers every service.
        /// </summary>
        /// <remarks>
        /// <see cref="SimulationRunner"/> is taken from the scene rather than constructed
        /// here. It is a MonoBehaviour because it has to sit on an object Unity ticks, and a
        /// pure C# object would need its own Update — which would put a second, competing
        /// driver of the Core clock into the project.
        /// </remarks>
        protected override void Configure(IContainerBuilder builder)
        {
            builder.RegisterComponentInHierarchy<SimulationRunner>();

            builder.Register<TableService>(Lifetime.Singleton).As<IProjectSpyService>();
            builder.Register<SaveService>(Lifetime.Singleton).As<IProjectSpyService>();
            builder.Register<SceneRouter>(Lifetime.Singleton).As<IProjectSpyService>();
            builder.Register<WindowService>(Lifetime.Singleton).As<IProjectSpyService>();
            builder.Register<ToastService>(Lifetime.Singleton).As<IProjectSpyService>();
            builder.Register<AudioService>(Lifetime.Singleton).As<IProjectSpyService>();
            builder.Register<LocalizationService>(Lifetime.Singleton).As<IProjectSpyService>();
            builder.Register<InputService>(Lifetime.Singleton).As<IProjectSpyService>();
        }

        /// <inheritdoc/>
        public void Initialize()
        {
            // Resolution order matters only in that Localization comes first: it is the one
            // service other services may need in order to turn a key into text.
            Localization = Container.Resolve<LocalizationService>();
            Tables = Container.Resolve<TableService>();
            Saves = Container.Resolve<SaveService>();
            Scenes = Container.Resolve<SceneRouter>();
            Windows = Container.Resolve<WindowService>();
            Toasts = Container.Resolve<ToastService>();
            Audio = Container.Resolve<AudioService>();
            Input = Container.Resolve<InputService>();
            Simulation = Container.Resolve<SimulationRunner>();

            LoadTables();

            Loaded = true;
        }

        /// <summary>True once every service has been resolved.</summary>
        public bool Loaded { get; private set; }

        /// <summary>
        /// Loads the compiled balance tables into Core.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is boot's job and not a caller's, because Core's own lazy table search
        /// cannot work inside Unity: it walks up from <c>AppContext.BaseDirectory</c>, which
        /// is the Editor <em>install</em> directory rather than this repository, so it always
        /// fails and every balance rule quietly falls back to its documented default. A
        /// Unity build missing this line runs a game that looks entirely correct and ignores
        /// every number in <c>data/*.csv</c> — which is the most expensive kind of bug there
        /// is, because nothing is broken and everything is wrong.
        /// </para>
        /// <para>
        /// A failure here is logged rather than thrown. The game is still playable on
        /// fallback numbers, and a boot that refuses to start is a harder problem to
        /// diagnose than one that says exactly which files it could not find.
        /// </para>
        /// </remarks>
        private void LoadTables()
        {
            try
            {
                Tables.Load();
            }
            catch (System.Exception ex)
            {
                Debug.LogError(
                    "[ProjectSpy] Could not load the compiled balance tables. The game is now " +
                    "running on Core's documented fallback numbers, which is not the same " +
                    $"game. Run 'pwsh tools/sync-dlls.ps1'. ({ex.Message})");
            }
        }
    }

    /// <summary>Shared filesystem locations, named in one place.</summary>
    public static class ProjectSpyPaths
    {
        /// <summary>Folder the save service writes into, under persistentDataPath.</summary>
        public const string SaveFolder = Persistence.SaveService.FolderName;
    }
}