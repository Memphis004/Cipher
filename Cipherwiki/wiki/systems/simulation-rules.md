---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, tables, luban, core, balance]
---

# SimulationRules — ทางเข้าสู่ตัวเลข balance

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

ตั้งแต่ Stage 2 **ไม่มี tuneable value ที่ hard-code ใน Core** ทุกอย่างอยู่ใน `data/*.csv` compile โดย Luban เป็น `ProjectSpy.Tables` แล้วอ่านกลับผ่าน accessor **สองตัว**:

| Accessor | ดูแล |
|---|---|
| `RoomDefinitions` | ห้อง |
| `SimulationRules` | ทุกอย่างที่ Stage 3 เพิ่ม |

## ทำไมต้องมี accessor แทนการอ่านตรง

`SimulationRules` มีอยู่เพื่อให้ rule system ขอเป็น **ชื่อ** แทนที่จะเป็น **column index**:

- มี**ที่เดียว**ที่ต้องดูเมื่อ designer ถามว่า "33 มาจากไหน"
- test สามารถ assert ว่า rule **ถูกหลังการข้อมูลจริง** ไม่ใช่ fallback

## `AreTablesLoaded` — ทำให้ fallback ซื่อสัตย์

> "Every accessor **degrades to a documented default** when the tables are absent — `AreTablesLoaded` lets a test tell 'the rule returned its fallback' from 'the rule returned real data', which is **what keeps the fallback honest**"

นี่เป็นรูปแบบที่ดี: การมี fallback อาจซ่อนบั๊กได้ (ข้อมูลหายเงียบๆ แล้วเกมยังเล่นได้ด้วยค่า default) — `AreTablesLoaded` ทำให้ test จับได้ว่าเกมกำลังวิ่งบน fallback

**ทำไม accessor ต้อง tolerant:** stage 5 **จงใจ**โหลด save ที่อาจอ้างถึง id ที่ถูกถอดออกแล้ว ตรงกับ [[data-integrity]] — accessor คืน default ที่ document ไว้แทนที่จะ throw

> "**Missing data is a build failure (`TableValidator`), not a runtime crash.**"

## Core อ้าง `ProjectSpy.Tables` โดยตรง

นี่ได้รับอนุญาตและ**ไม่ละเมิด Unity boundary**:

> "the tables assembly is **pure compiled data** (beans and enums), contains no Unity types, and targets `netstandard2.1` like everything else"

ตอบคำถามที่ค้างอยู่ใน [[magic-numbers]]: dependency เป็น **direct reference** ไม่ใช่การ inject

```
  data/*.csv  ──pwsh tools/gen.ps1──>  src/ProjectSpy.Tables/Gen   (C# beans)
                                         assets/data/tables          (*.bytes)
                                                  │
                                                  ▼
  Core ── SimulationRules.RecoveryFor(4001).MentalRatioPercent ──> read at runtime
```

> **เป็นการอนุมาน:** ตัวอย่าง `RecoveryFor(4001)` แสดงว่าการเข้าถึงเป็น **key ตัวเลข 4001** — สอดคล้องกับกฎ "id space disjointness: each key-value table owns its own band" ใน [[systems/table-validator]] ซึ่งแปลว่า id space เป็นตัวเลขช่วง ไม่ใช่ชื่อ

## เชื่อมโยง

- [[magic-numbers]] — กฎข้อ 3 ที่ accessor นี้คือกลไกบังคับใช้
- [[systems/table-validator]] — test ที่พิสูจน์ว่าตารางไม่พัง
- [[data-integrity]] — กฎข้อ 7 และเรื่อง quarantine id
- [[systems/game-session]] — "no third-party dependencies" ของ Core
- [[systems/core-purity]] — Tables เป็น first-party ที่ target เดียวกัน
- [[systems/grid-commands]] — translation ที่ Core ใช้ร่วมกับ UI
- [[design/base-building]] — ค่า depth cost ที่อ่านจากตาราง
- [[systems/mole-system]] — counter-intel balance ที่ validate
- [[stage-order]] — Stage 2 คือจุดที่กฎนี้เริ่มมีผล
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] `SimulationRules` มีกี่กฎ และ id band แต่ละตารางคือช่วงไหน
- [ ] Fallback default ของแต่ละกฎคืออะไร และ document ไว้ที่ไหน
- [ ] `AreTablesLoaded` เป็น static flag หรือผ่าน DI — ถ้าเป็น static จะขัดกับ "no singletons" ที่ระบุในสำเนา `knowledge.md` ดู [[sources/GDD]]
- [ ] Test ไหน assert ว่า rule ถูกหลังข้อมูลจริง (ไม่ใช่ fallback)
