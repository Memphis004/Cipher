---
type: source
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, architecture, core, implementation]
---

# ARCHITECTURE.md — โครงสร้างการทำงานของ Core และ Presentation

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

สรุปหนึ่งย่อหน้า: เอกสารนี้อธิบาย **กลไกจริง** ที่หลังกฎใน [[sources/knowledge]] — assembly สามตัวที่โหลด `ProjectSpy.Core.dll` เดียวกัน, command/event flow, tick pipeline, กลไก determinism สี่ชั้น, การย้ายจาก grid เป็น slot, และที่มาของตัวเลข balance

> "Updated at stage 2." — เอกสารนี้เขียน/ปรับ ณ Stage 2 แต่มีเนื้อหาของ Stage 3 ปนอยู่แล้ว

## ทำไมเอกสารนี้สำคัญ

`knowledge.md` บอกว่า*อะไร* ต้องเป็นจริง ARCHITECTURE.md บอกว่า*มันทำงานยังไง* และบอกว่าอะไร**ยังไม่ได้ทำ** หน้านี้เชื่อมกฎกับ implementation จริง

## หัวข้อในเอกสาร

| หัวข้อ | เนื้อหา | หน้า |
|---|---|---|
| Layering | diagram ของ assembly ทั้งสาม + Core internals | [[systems/game-session]] |
| Command flow | `Execute` → `Validate` → `Apply`, double-validate | [[systems/game-session]] |
| Event flow | pending buffer + `FlushEvents` | [[systems/game-event-buffer]] |
| Time and the tick pipeline | `GameClock`, เหตุผลของลำดับแต่ละคู่ | [[systems/game-clock]], [[systems/tick-pipeline]] |
| Phase implementations | ทุก phase implement แล้ว ณ Stage 3 | [[systems/mole-system]] |
| No coordinates in Core | ตาราง Was/Is ของการย้าย grid → slot | [[systems/grid-to-slot-migration]] |
| Determinism | กลไก 4 ชั้น + `ComputeStateHash` | [[systems/world-state]], [[systems/replay]] |
| The replay input | `ReplayEntry`, ปัญหา parallel log | [[systems/replay]] |
| The two determinism tests | test 8 ตัว + counterweights | [[systems/determinism-tests]] |
| Where balance numbers come from | `SimulationRules`, `AreTablesLoaded` | [[systems/simulation-rules]] |
| Table integrity | `TableValidator` 4 กฎ | [[systems/table-validator]] |
| What is deliberately not here yet | Stage 4/5/7 ที่ยังไม่มา | [[systems/tick-pipeline]] |

## ข้อยืนยันความสอดคล้อง

ARCHITECTURE.md ยืนยันกฎข้อ 2, 6, 8, 10 และ 3 ใน [[sources/knowledge]] **โดยตรง** และยังยืนยันจุดที่ GDD เขียนไว้ว่ามี 5 RNG streams (ดู [[systems/irng-streams]]) — จึงยืนยันว่าสำเนา `knowledge.md` ที่ฝังใน GDD.md เป็นรุ่นเก่า ดู [[sources/GDD]]

## เชื่อมโยง

- [[sources/knowledge]] — กฎที่บังคับใช้
- [[sources/GDD]] — design intent ที่เอกสารนี้ implement
- [[sources/README]] — สถานะ stage และ build commands
- [[systems/grid-to-slot-migration]] — การย้าย grid → slot คือตัวอย่างที่ชัดที่สุดของการบังคับใช้กฎข้อ 10
- [[determinism-tests]] — test ที่พิสูจน์กฎข้อ 6

## หมายเหตุ

`raw/FLOORPLAN_UI.md` และ `raw/MOLE_DESIGN.md` ถูกอ้างถึงในส่วน "Where to read next" — ยังไม่ได้ ingest
