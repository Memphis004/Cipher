---
type: system
source: raw/README.md
updated: 2026-10-02
tags: [cipher, layout, build, tooling]
---

# Project Layout และคำสั่ง build

> **แหล่งที่มา:** [raw/README.md](../../raw/README.md)
> **อัปเดต:** 2026-10-02

## โครงสร้างตามที่ `README.md` ระบุ

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

## ในโครงสร้างจริงบนดิสก์

`knowledge.md` และโฟลเดอร์ `docs/` **อยู่ใต้ `raw/`** ใน vault นี้ เพราะถูกย้ายมาเป็น raw sources ของ wiki — `AGENTS.md` ระบุว่า `raw/` เป็น read-only และการแก้ไขโครงสร้าง repo เป็นการตัดสินใจของผู้ใช้ ไม่ใช่ของ wiki

เอกสารอ้างอิงภายในที่ยัง**ไม่มีไฟล์**: `docs/SAVE-FORMAT.md` และ `docs/BALANCE.md`

## Target framework

| Assembly | Target | บทบาท |
|---|---|---|
| `ProjectSpy.Core` | netstandard2.1 | pure simulation — ดู [[systems/game-session]] |
| `ProjectSpy.Tables` | netstandard2.1 | Luban output, first-party ที่ Core อ้างตรง ดู [[systems/simulation-rules]] |
| `ProjectSpy.Core.Tests` | net8.0 | xUnit |
| `ProjectSpy.Sim` | net8.0 | headless balance simulator (Stage 6/10) |
| `UnityProject` | — | Unity presentation (Stage 7-10) |

การที่ **Core และ Tables target เดียวกัน** คือเหตุผลที่ direct reference ไม่ละเมิด Unity boundary ดู [[core-purity]]

## Build และ test

```bash
dotnet build ProjectSpy.sln
dotnet test src/ProjectSpy.Core.Tests/ProjectSpy.Core.Tests.csproj
```

ต้องใช้ .NET SDK (พัฒนากับ 9.0; `netstandard2.1` และ `net8.0` build ได้จาก SDK ≥ 8)

`Directory.Build.props` ตั้ง **LangVersion latest, Nullable + ImplicitUsings** ทั้ง solution — สอดคล้องกับ style rule ในสำเนา `knowledge.md` (C# file-scoped namespaces, nullable enabled, `sealed` by default) ดู [[sources/GDD]]

## Regenerating data tables

```bash
pwsh tools/gen.ps1     # ต้องมี tools/luban/Luban.dll; ถ้าไม่มีจะพิมพ์วิธีดาวน์โหลด
```

`gen.ps1` เขียน compiled C# ไปที่ `src/ProjectSpy.Tables/Gen` และ binary ไปที่ `assets/data/tables`

> ดู [[sources/README]] — README อ้างถึง `data/README.md` สำหรับ CSV format, id spaces และ enum conventions แต่ไฟล์นี้**ไม่มีอยู่ใน `raw/`**

## `tools/sync-dlls.ps1` (Stage 7)

ยังไม่มีรายละเอียด — มีแต่ชื่อ คาดว่า copy DLL ของ `ProjectSpy.Tables` ไป `Assets/StreamingAssets` ตามที่ [[sources/ARCHITECTURE]] ระบุว่าจะเกิดใน Stage 7

## ⚠️ `src/ProjectF.Tables` — ไดเรกทอรีค้างที่ประกาศ namespace ชนกัน

> เกมตกปลารุ่นก่อนที่ประกาศ namespace `ProjectSpy.Tables` **เดียวกับ** assembly จริง Stage 2 เอาออกจาก solution แล้ว แต่**ไดเรกทอรียังอยู่บนดิสก์ ไม่ถูกแตะและไม่ถูกอ้างถึง**

**ความเสี่ยง:** ถ้ามีโค้ดอ้าง namespace `ProjectSpy.Tables` โดยไม่ตั้งใจ อาจ resolve ไปยังชุดข้อมูลของเกมตกปลา — และอาการจะเป็น "ค่าตัวเลขมั่ว" ไม่ใช่ error ชัดเจา ซึ่งตรงกับหลักการใน [[systems/table-validator]]: balance error ที่ไม่ให้ error message คือแบบที่แย่ที่สุด

**แนะนำ:** ลบไดเรกทอรี แต่เป็นการเปลี่ยนโครงสร้าง repo — ให้ผู้ใช้ตัดสินใจ ไม่ใช่ wiki

## เชื่อมโยง

- [[sources/README]] — หน้าต้นทาง
- [[systems/game-session]] — assembly ที่โหลด Core.dll เดียวกัน
- [[systems/simulation-rules]] — Tables และ pipeline การ generate
- [[systems/tech-stack]] — เครื่องมือทั้งหมด
- [[systems/table-validator]] — test ที่รันผ่าน `dotnet test`
- [[core-purity]] — เหตุผลของการเลือก netstandard2.1
- [[stage-order]] — stage ที่แต่ละส่วนของโครงสร้างจะถูกสร้าง
- [[data-integrity]] — save format ที่ยังไม่มี
- [[sources/knowledge]] — กฎที่บังคับใช้
