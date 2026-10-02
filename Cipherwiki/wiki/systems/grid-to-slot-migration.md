---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, slots, layer, migration]
---

# Grid → Slot migration — การถอดพิกัดออกจาก Core

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

ฐานเดิมเป็น **lattice `Width` x `Height` พร้อม occupancy array** และห้องรู้ cell ของตัวเอง — ซึ่ง **ทำให้การตัดสินใจด้าน 2D หลุดเข้าไปใน simulation** ตอนนี้เปลี่ยนแล้ว:

| Was | Is |
|---|---|
| `GridX`, `GridY`, `Width` | `SlotIndices` (a set), `Layer` |
| adjacency derived by comparing coordinates | `AdjacentRoomIds`, **explicit และ symmetric** |
| `RoomAt(x, y)` | `RoomInSlot(layer, slot)` |
| `RoomsAtDepth(y)` | `RoomsAtLayer(layer)` |
| `OccupiedCellCount` / `OccupancyPercent` | `OccupiedSlotCount` |
| `PlacementError.OutOfBounds` / `OverlapsExisting` / `NonPositiveWidth` | `LayerOutOfRange` / `SlotOccupied` / `NoSlots` |

## สองอย่างที่รักษาไว้โดยเจตนา

เพราะเป็น **กฎ ไม่ใช่ rendering**:

- **Layer** — depth เป็น rule input: ห้องใน layer ลึกกว่ามีต้นทุนก่อสร้างสูงกว่า (`ApplyDepthCost`) เฉพาะ **การตีความ** ของ layer เท่านั้นที่เป็นเรื่อง presentation
- **Merge-adjacency** — ห้องสองห้องยังถูกกำหนดให้แชร์ layer ซึ่งกันไม่ให้ห้องกลืนห้องที่วางอยู่ด้านบน ตรงกับกฎ merge ใน [[design/base-building]]

## สองผลกระทบที่ควรพูดตรงๆ

### Adjacency เป็น data แล้ว ระยะจึงไม่ได้แปลว่าติดกัน

> "Two rooms can be numerically side by side and **not touch**, because Presentation decides who touches whom. Anything that wants grid behaviour **has to declare it**"

นี่คือผลข้างเคียงที่แท้จริง: กฎข้อ 10 บังคับให้ adjacency เป็น explicit data เพราะพิกัดไม่ replay ได้ ผลคือ **Presentation ต้องตัดสินว่าห้องไหนติดกัน** ซึ่งคืองานที่ floorplan UI ต้องทำอยู่แล้ว (`raw/doc/FLOORPLAN_UI.md` ยังไม่ได้ ingest)

### Placement เป็น command ที่รวมข้อมูลมากขึ้น

`BuildRoomCommand` ถือ:

```
(TypeId, Layer, SlotIndices, AdjacentRoomIds, NameKey, TotalCost)
```

แทนตำแหน่ง grid — และ **`CorePurityTests` จะ fail build ถ้า member ที่มีรูปพิกัดกลับมา** ดู [[systems/core-purity-tests]]

## ทางออกสำหรับ test code

Test code ที่อยากคิดเป็น grid ใช้ **`GridCommands.Build` และ `World.Place`** ซึ่งแปลง grid triple เป็น slots, layer และ adjacency ที่ derive แล้ว — **การแปลงเดียวกับที่ floorplan UI จริงทำ**

> "That kept **~115 call sites** unchanged while Core stayed coordinate-free"

นี่เป็นตัวอย่างที่ดีของ [[scope-discipline]]: เปลี่ยนโครงสร้างขนาดใหญ่โดยไม่ต้องแตะ call site ที่ไม่เกี่ยวข้อง

## สิ่งที่ยังไม่เสร็จ

> "`ICommand` **still has no tick cost**. The rule requires every rule-affecting action to carry one, and today commands apply instantly; the first timed actions arrive with **stage 4's mission interiors**"

นี่คือ **ช่องว่างที่ยังอยู่ระหว่างกฎกับ implementation** ของกฎข้อ 10 และกฎข้อ 6: กฎบอกว่าทุก action ที่มีผลต่อกฎต้องมี tick cost แต่ตอนนี้ยังไม่มี command ไหนมี ดู [[systems/command-log]] และ [[design/mission-and-fog-of-war]]

## เชื่อมโยง

- [[slot-layer-abstraction]] — กฎข้อ 10 ที่ migration นี้คือการบังคับใช้
- [[systems/core-purity-tests]] — ตัวที่จับการย้อนกลับไปใช้พิกัด
- [[systems/command-log]] — `BuildRoomCommand` ที่เปลี่ยนรูป
- [[systems/grid-commands]] — `GridCommands.Build` / `World.Place` สำหรับ test
- `raw/doc/FLOORPLAN_UI.md` — ยังไม่ได้ ingest: contract ของ slot ↔ screen mapping สำหรับ Stage 8
- [[design/base-building]] — merge rule และ depth cost ที่รักษาไว้
- [[design/art-direction]] — 96×64 px ต่อช่องห้อง เป็นเรื่อง presentation
- [[determinism]] — "any coordinate in Core is **determinism debt**"
- [[core-purity]] — กลไกบังคับใช้ระดับ assembly
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] `SlotIndices` เป็น set ของอะไร — slot index เป็น integer ต่อห้อง หรือต่อช่อง
- [ ] เดิม ~115 call sites เป็นของ production หรือ test ทั้งหมด
- [ ] `AdjacentRoomIds` symmetric ถูกบังคับใช้ด้วย test หรือเป็นแค่วินัย
- [ ] tick cost จะถูกเพิ่มเข้า `ICommand` ใน Stage 4 รูปแบบไหน — field, หรือผ่าน `AdvanceTick`
