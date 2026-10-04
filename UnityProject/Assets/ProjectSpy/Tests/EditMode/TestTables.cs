using NUnit.Framework;
using ProjectSpy.Core;
using ProjectSpy.Unity.Persistence;

namespace ProjectSpy.Unity.Tests
{
    /// <summary>
    /// Loads the compiled balance tables once for every EditMode test in this namespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="SetUpFixtureAttribute"/> rather than a helper each fixture remembers to
    /// call, because the failure it prevents is silent in exactly the way a helper invites:
    /// a test that asserts <c>AreTablesLoaded</c> in its own setup fails loudly, but a test
    /// that does not remember to assert it compares Core's documented fallback defaults
    /// against Core's documented fallback defaults and passes. Both halves of a
    /// disagreement are wrong in the same direction.
    /// </para>
    /// <para>
    /// This is the same defect the game had. Core's own table loader walks up from
    /// <c>AppContext.BaseDirectory</c>, which inside Unity is the Editor <em>install</em>
    /// directory rather than this repository, so it always fails — and every rule silently
    /// ran on its fallback. Presentation's <see cref="TableService"/> exists to resolve the
    /// binaries from StreamingAssets and hand them to Core explicitly, which is exactly what
    /// this does.
    /// </para>
    /// </remarks>
    [SetUpFixture]
    public sealed class TestTables
    {
        /// <summary>The loaded tables, or null when the binaries are missing.</summary>
        public static TableService Service { get; private set; }

        [OneTimeSetUp]
        public void LoadTables()
        {
            Service = new TableService();

            try
            {
                Service.Load();
            }
            catch (System.Exception ex)
            {
                // Reported rather than thrown. A SetUpFixture that throws fails every test
                // in the namespace with the same message and hides which assertion would
                // have failed; asserting that the tables really loaded leaves the question
                // in the place where it can be answered.
                UnityEngine.Debug.LogError(
                    "[ProjectSpy] EditMode tests could not load the compiled balance tables. " +
                    "Every rule will run on Core's fallback numbers, and any test asserting " +
                    $"agreement between Core and Presentation will agree on nothing. " +
                    $"Run 'pwsh tools/sync-dlls.ps1'. ({ex.Message})");
            }
        }

        /// <summary>
        /// Asserts the tables are loaded, for a fixture that wants to say so explicitly.
        /// </summary>
        public static void Require()
        {
            Assert.That(SimulationRules.AreTablesLoaded, Is.True,
                "Core tables are not loaded, so this test would compare fallback defaults " +
                "against fallback defaults and pass without proving anything. Run " +
                "'pwsh tools/sync-dlls.ps1'.");
        }
    }
}