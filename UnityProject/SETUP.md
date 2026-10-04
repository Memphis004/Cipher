# Unity setup (ProjectSpy)

2.5D URP 3D on **Unity 6.3 LTS (6000.3.9f1)**. Everything in `Assets/ProjectSpy` is
generated or hand-written by the tools below; nothing here needs hand-fixing after a
clone.

## Running the setup

Two commands, from the repository root:

```bash
pwsh tools/gen.ps1                      # CSVs -> compiled tables + Core table beans
pwsh tools/sync-dlls.ps1                # Core + Tables DLLs into Assets/Plugins/ProjectSpy
```

Then, once Unity has imported the project:

**Project menu → `ProjectSpy/Setup/Run Full Setup`**

That menu command creates the Forward+ renderer, wires the SRP batcher, generates the
blockout art and prefabs, creates the `Boot` / `Base` / `Tactical` scenes with `Boot` at
build index 0, and validates the result. It is idempotent; run it again after any change
to the Core sources.

## What is where

| Path | What it is |
|---|---|
| `Assets/ProjectSpy/Scripts/Runtime` | `ProjectSpy.Unity` — everything that ships |
| `Assets/ProjectSpy/Scripts/Editor` | `ProjectSpy.Unity.Editor` — setup tooling, validators |
| `Assets/Plugins/ProjectSpy` | Core and Tables DLLs, written by `sync-dlls.ps1` |
| `Assets/Packages` | MessagePack, via NuGetForUnity. **Do not also copy these into Plugins.** |
| `assets/data/tables` | Shared table binaries; `gen.ps1` owns them |

## The rules this layout exists to protect

**Presentation never computes.** Core decides how far a guard sees and whether a door can
be opened; Unity draws that decision. `ProjectSpy/Validate Presentation Rules` flags any
arithmetic on a Core stat or position field. `LaneUnits` is the one sanctioned place where
Core centimetres become Unity metres, and the validator skips it deliberately.

**A speed is a clock, not a rule.** `SimulationRunner` converts real seconds into ticks or
steps and calls `AdvanceTick` / `AdvanceTacticalStep`. Pausing stops calling `Advance` and
changes nothing about what `Advance` does. Rendering interpolates via `RenderAlpha`; the
simulation never does.

**One MessagePack.** `Assets/packages.config` pins MessagePack to the version Core compiles
against. `sync-dlls.ps1` skips it for that reason. Two copies is a `TypeLoadException` far
from its cause; `ProjectValidator` fails the build if one reappears in `Plugins`.

**Core loads its tables by walking up from the DLL path, which does not work in Unity**
(`AppContext.BaseDirectory` is the Editor *install* directory there). Unity's
`TableService` loads from StreamingAssets and hands the result to
`SimulationRules.UseTables`. Without that the game runs silently on fallback balance
numbers with no error anywhere.

---

## Remaining manual steps

Three, and they are all one-time.

### 1. Open the project in Unity 6.3 and let it import

First import after a clone reimports the package cache and takes several minutes. Wait for
the spinner to stop before running anything below — running `Run Full Setup` mid-import
produces confusing partial-asset errors.

### 2. Steamworks.NET

Not integrated, as specified. Download the **Steamworks.NET** redistributable
(`Steamworks.NET` SDK zip from partner.steamgames.com, `redistributable_bin/`) and unzip it
into `Assets/Plugins/Steamworks.NET/`. Nothing in `ProjectSpy.Unity` references it yet;
`IPlatformService` is the seam stage 10 implements. Until then the game runs with no Steam
client, which is the intended behaviour, not a bug.

### 3. Copy the table binaries into StreamingAssets

`gen.ps1` writes to `assets/data/tables`, which Unity cannot read from. Copy that folder's
contents to `Assets/StreamingAssets/ProjectSpyTables/`.

This is automated in CI and in `sync-dlls.ps1` on machines where the path resolves; it is
listed here because a fresh clone on a new machine has no `StreamingAssets` yet.

## Verifying the setup

- **ProjectSpy → Validate Project** — should report zero errors. It checks the renderer is
  3D-capable, `Boot` is at build index 0, the Core DLLs are present, and that no assembly
  in `Assets/Plugins/ProjectSpy` also exists elsewhere in the project. That last one is
  checked by name across the whole project rather than for `MessagePack.dll` alone,
  because NuGetForUnity installs a package's whole transitive closure: the duplicates that
  actually occurred were `Microsoft.Bcl.AsyncInterfaces.dll` and
  `System.Collections.Immutable.dll`, both published over their NuGet copies by
  `sync-dlls.ps1` and silently discarded by Unity.
- **ProjectSpy → Setup → Site Preview (50 buildings)** — generates fifty buildings from the
  real tables. This is the real test that `SiteAssembler` handles any layout without manual
  fixing: look for templates that always produce the same shape and rooms too narrow to
  read.

## Known gaps at the end of stage 7

- **A saved campaign cannot be loaded.** `SaveService.SaveWorld` writes Core's canonical
  bytes and verifies them, but Core has no world deserialiser, so `TryLoadWorld` returns
  false and says so. Adding a Unity-side format now would mean writing one twice.
  `SiteLayout` does round-trip today via `SiteLayout.FromSaveData`.
- **Localization rows are not wired to the table yet.** `LocalizationService` resolves keys
  correctly and falls back as documented, but nothing populates it from the
  `localization` table at boot. Stage 8 does this.
- **Stage 9a lighting.** One directional light per scene is set up so the blockout reads;
  the per-room key lights that make lighting a mechanic come with the tactical camera.