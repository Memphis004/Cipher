---
type: source
source: raw/README.md
updated: 2026-10-02
tags: [cipher, readme, layout, stages]
---

# README.md — โครงสร้าง repo, สถานะ stage และคำสั่ง build

> **แหล่งที่มา:** [raw/README.md](../../raw/README.md)
> **อัปเดต:** 2026-10-02

สรุปหนึ่งย่อหน้า: README เป็น**เอกสารที่บอกว่าโค้ดอยู่ที่ไหนและสถานะปัจจุบันคืออะไร** — คู่กับ `knowledge.md` ที่บอกกฎ และ `ARCHITECTURE.md` ที่บอกกลไก ตัว README ยืนยัน layout, build commands และรายการของ Stage 3 ที่ส่งมอบแล้ว

## โครงสร้าง repo

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

> **หมายเหตุ:** ในโครงสร้างจริงบนดิสก์ `knowledge.md` และ `docs/` อยู่ใต้ `raw/` (เพราะถูกย้ายมาเป็น raw sources ของ wiki) — ดู [[project-layout]]

## Stage status — ตารางที่ยืนยัน

| Stage | Scope | สถานะ |
|-------|-------|--------|
| 1 | Solution skeleton + domain model | **done** |
| 2 | Data tables (Luban) | **done** |
| 3 | Simulation core: agents, rooms, economy | **done** |
| 4 | Mission generation + fog of war | not started |
| 5 | Save/load, replay, determinism | not started |
| 6 | Headless playability + balance harness | not started |
| 7 | Unity bootstrap + editor automation | not started |
| 8 | UI framework + Base scene | not started |
| 9 | Mission scene + game feel | not started |
| 10 | Meta, Steam, polish, release | not started |

**ยืนยันข้อมูลเดิมใน [[concepts/stage-order]]** ซึ่งเคยติดป้าย `เป็นการอนุมาน` — ตอนนี้ยืนยันแล้ว ต้องเอาป้ายออก

**สอดคล้องกับ Stage 3 ที่ `ARCHITECTURE.md` อ้างว่า phase ทั้งหก implement แล้ว** ([[systems/tick-pipeline]])

## Stage 3 ส่งมอบอะไร

> "the six-phase tick pipeline, training, recovery and burnout, **weekly settlement with loans and a bankruptcy ladder**, recruitment, **loyalty drift and its escalation ladder**, and **the mole with counter-intel**"

สอง headline guarantee ที่ทดสอบแล้ว:

- **365-day unattended run** อยู่ในกฎทางการเงินและ reproducible
- **สอง session ที่ใช้ seed และ command log เดียวกันให้ byte-identical state**

ตรงกับ [[systems/determinism-tests]] พอดี

## ข้อมูลที่ยืนยัน/เติมเต็มจุดค้าง

| คำถามที่ค้าง | คำตอบ |
|---|---|
| `docs/SAVE-FORMAT.md` อยู่ที่ไหน | มีการอ้างถึงใน README แต่ **ไม่มีไฟล์ใน `raw/`** — ยังไม่ชัวร์ |
| Stage 3 tick cost ของ command | README ยืนยันว่า Stage 3 ส่งมอบ tick pipeline แต่ไม่ได้พูดถึง tick cost — ยังเป็นช่องว่างเดิม ดู [[systems/grid-to-slot-migration]] |
| `ProjectSpy.Tables` เข้า Core ยังไง | `src/ProjectSpy.Tables/ # netstandard2.1 — Luban output` → **direct reference** ยืนยันจาก [[systems/simulation-rules]] |
| `docs/BALANCE` | อ้างถึงใน README แต่ไม่มีใน `raw/` |

## ⚠️ `TableValidator` — README ให้รายการที่ยาวกว่า ARCHITECTURE.md

README ระบุว่า `dotnet test` รัน `TableValidator` ซึ่ง fail build เมื่อ:

1. **dangling foreign key**
2. **non-positive weight**
3. **missing localization key**
4. **unknown skill reference**
5. **mission tier with fewer than three usable node rooms**

ARCHITECTURE.md ระบุกฎอื่น: counter-intel balance, recovery room ratio, required keys per rule table, id space disjointness ([[systems/table-validator]])

> **ข้อสังเกต:** สองรายการนี้ **ไม่ทับกันเลย** แปลว่า validator มีอย่างน้อย 9 กฎ และ README ให้รายการที่คนละชุดกับ ARCHITECTURE.md เอกสารทั้งสองอ้างถึง test ตัวเดียวกันแต่อธิบายคนละส่วน — ต้องยืนยันกับโค้ดว่ากฎไหนอยู่จริง
>
> ข้อ 5 ("mission tier with fewer than three usable node rooms") น่าสนใจเป็นพิเศษ เพราะ [[systems/tick-pipeline]] ระบุว่า Stage 3 **ยังไม่มีการ generate mission** — validator นี้จึงตรวจข้อมูล mission ที่ยังไม่ถูกใช้ ซึ่งสอดคล้องกับการเตรียมข้อมูลไว้ล่วงหน้า

## Build และ test

```bash
dotnet build ProjectSpy.sln
dotnet test src/ProjectSpy.Core.Tests/ProjectSpy.Core.Tests.csproj
```

ต้องใช้ .NET SDK (พัฒนากับ 9.0; `netstandard2.1` และ `net8.0` build ได้จาก SDK ≥ 8)

## เรื่อง `src/ProjectF.Tables` ที่ยังอยู่บนดิสก์

> "`src/ProjectF.Tables` held an earlier, **unrelated fishing-game table set** และประกาศ namespace `ProjectSpy.Tables` เดียวกัน Stage 2 แทนที่ `data/` ด้วย schema ของ CIPHER และ **removed it from the solution**; the directory itself is **still on disk, untouched and unreferenced**"

นี่คือ **กับดัก namespace ที่ยังค้างอยู่**: ไดเรกทอรีเก่าประกาศ namespace เดียวกับของจริง ถ้ามีการอ้าง namespace นั้นโดยไม่ตั้งใจ อาจ resolve ไปยังชุดข้อมูลของเกมตกปลาแทน ควรลบเมื่อสะดวก — แต่นี่เป็นการตัดสินใจเรื่องโค้ด ต้องให้ผู้ใช้ตัดสินใจ

## Architecture in one paragraph

> Presentation สร้าง `ICommand` เรียก `GameSession.Execute` อ่าน result พร้อม stream ของ `GameEvent` — **Core เป็นสิ่งเดียวที่ mutate `WorldState`** ทุก mutation จึงตกอยู่ใน `CommandLog` ที่เรียงตามลำดับ และทั้งเกม replay ได้จาก seed

ตรงกับ [[systems/game-session]] และ [[systems/game-event-buffer]]

## เชื่อมโยง

- [[sources/knowledge]] — กฎที่บังคับใช้
- [[sources/ARCHITECTURE]] — กลไกของ Stage 2/3 ที่ README สรุป
- [[sources/GDD]] — design intent ที่ยังไม่ implement ถึง Stage 4
- [[project-layout]] — โครงสร้างไฟล์และ build commands
- [[systems/determinism-tests]] — headline guarantee ที่ README อ้างถึง
- [[systems/table-validator]] — รายการ validate ที่ยังต้องยืนยัน

## คำถามที่ยังไม่มีคำตอบ

- [ ] `docs/BALANCE.md` ถูกอ้างถึงใน README แต่ไม่มีใน `raw/` — จะเขียนเมื่อไร
- [ ] `tools/sync-dlls.ps1` (Stage 7) ทำอะไร — คัดลอก DLL ของ Tables ไป Unity
- [ ] "loans and a bankruptcy ladder" และ "loyalty drift and its escalation ladder" ยังไม่มีหน้าใน wiki — ควรเป็น design pages
