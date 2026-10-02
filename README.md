# Project CIPHER

A single-player espionage management game for Steam: run a clandestine agency,
train and deploy operatives, and survive the heat you generate.

The guiding constraint is **simulation first**. The game lives in
`ProjectSpy.Core`, a pure C# simulation library. Unity is a presentation layer
over that library and is not allowed to contain game rules. If the game is not
fun in a terminal, no amount of art will save it.

## Non-negotiable rules

See [knowledge.md](knowledge.md). The two that shape every file:

- **`ProjectSpy.Core` must never reference `UnityEngine`** (or `System.Random`,
  `DateTime`, `Guid.NewGuid`, `File`, `Environment`, or float arithmetic in rule
  paths). It targets netstandard2.1 so the identical assembly runs in Unity, in
  the console simulator, and under xUnit.
- **Core never emits English prose for the player.** A rejected command returns a
  `ReasonCode` enum plus args; the UI maps that to localized text. Thai is the
  default language.

## Layout

```
ProjectSpy/
├── ProjectSpy.sln
├── Directory.Build.props       # LangVersion latest, Nullable + ImplicitUsings
├── knowledge.md                # binding rules for all stages
├── data/                       # Luban CSV (stage 2)
├── tools/                      # gen.ps1, sync-dlls.ps1 (stages 2 / 7)
├── docs/                       # ARCHITECTURE, SAVE-FORMAT, BALANCE
├── src/
│   ├── ProjectSpy.Core/        # netstandard2.1 — pure simulation
│   ├── ProjectSpy.Core.Tests/  # net8.0 — xUnit
│   ├── ProjectSpy.Tables/      # netstandard2.1 — Luban output (stage 2)
│   └── ProjectSpy.Sim/         # net8.0 — headless balance simulator (stage 6/10)
└── UnityProject/               # Unity, presentation only (stages 7–10)
```

> **Note on the pre-existing tables project.** `src/ProjectF.Tables` held an earlier,
> unrelated fishing-game table set and declared the same `ProjectSpy.Tables`
> namespace. Stage 2 replaced `data/` with the CIPHER schema and removed it from
> the solution; the directory itself is still on disk, untouched and unreferenced.
> `src/ProjectSpy.Tables` is now the real, Luban-generated tables assembly.

## Stage status

| Stage | Scope | Status |
|-------|-------|--------|
| 1 | Solution skeleton + domain model | **done** |
| 2 | Data tables (Luban) | **done** |
| 3 | Simulation core: agents, rooms, economy | not started |
| 4 | Mission generation + fog of war | not started |
| 5 | Save/load, replay, determinism | not started |
| 6 | Headless playability + balance harness | not started |
| 7 | Unity bootstrap + editor automation | not started |
| 8 | UI framework + Base scene | not started |
| 9 | Mission scene + game feel | not started |
| 10 | Meta, Steam, polish, release | not started |

## Build and test

```bash
dotnet build ProjectSpy.sln
dotnet test src/ProjectSpy.Core.Tests/ProjectSpy.Core.Tests.csproj
```

Requires the .NET SDK (developed against 9.0; `netstandard2.1` and `net8.0`
targets build from any SDK ≥ 8).

## Regenerating the data tables

All tuneable content is CSV in `data/`, compiled by Luban:

```bash
pwsh tools/gen.ps1     # needs tools/luban/Luban.dll; prints download instructions if absent
```

`gen.ps1` writes compiled C# to `src/ProjectSpy.Tables/Gen` and binary to
`assets/data/tables`. See [data/README.md](data/README.md) for the CSV format, the
id spaces and the enum conventions.

`dotnet test` runs `TableValidator`, which fails the build on a dangling foreign
key, a non-positive weight, a missing localization key, an unknown skill
reference, or a mission tier with fewer than three usable node rooms.

## Architecture in one paragraph

Presentation builds an `ICommand`, calls `GameSession.Execute`, and reads the
result plus a stream of `GameEvent` records. Core is the only thing that mutates
`WorldState`, so every mutation lands in an ordered `CommandLog` and the whole
game is replayable from a seed. Details and a diagram:
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
