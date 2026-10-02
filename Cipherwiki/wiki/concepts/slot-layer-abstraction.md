---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, core, slots, layer, architecture]
---

# Slot/Layer Abstraction — Core ไม่เก็บพิกัด

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 10)
> **อัปเดต:** 2026-10-02

**Core ไม่เก็บ world-space coordinates, mesh หรือ camera data เด็ดขาด** — ไม่มี position, size, rotation, transform, renderer, collider หรือแนวคิดเรื่อง viewport ที่ใดใน Core **รวมถึงใน type ที่มีอยู่เพื่อสนับสนุนการ render** ข้อความนี้ตั้งใจกัน type ที่ "แค่ช่วย render" ให้หลุดจากข้อห้าม

## สิ่งที่แทนพิกัด

- ฐาน: ห้องถูกระบุด้วย **slot index และ layer** ไม่ใช่ grid cell
- ภายใน mission node: อธิบายเป็น **`RoomContents`** — รายการของ interactables (container, door, terminal, guard, trap, exit) แต่ละอันมี stable id, type, state และ abstract slot index
- **Presentation layer เป็นผู้ตัดสินว่า slot แมปไปเป็นตำแหน่ง 2D, 2.5D หรือ 3D** Core นิยามว่าอันไหนอยู่ slot ไหน และไม่อย่างอื่น

## การเดินในห้องเป็นเรื่องของ presentation เท่านั้น

**ทุก action ที่มีผลต่อกฎคือ `ICommand` ที่มี tick cost** ถ้า Core เก็บ position ของ agent ผู้เล่นจะเดินได้โดยไม่ต้อง issue command และการเดินนั้นจะไม่อยู่ใน command log → replay ไม่ได้ → ละเมิด [[determinism]] ข้อ 6

## คำถามเชิงพื้นที่ที่*เป็น*กฎ

บางคำถามเรื่องพื้นที่ก็เป็นกฎจริง และเก็บเป็น abstract data ใน Core ได้:

- ห้องไหนอยู่ติดกัน (adjacency)
- ห้องอยู่ layer ไหน
- interactable ตัวหนึ่งอยู่ในสถานะอะไร

**Adjacency เป็น explicit data ไม่ใช่ค่าที่ derive มาจากพิกัด**

## Why — สามเหตุผล เรียงตามน้ำหนัก

1. **Determinism** — position ที่ถูกแก้โดยอะไรที่ไม่ใช่ command คือ position ที่ replay ไม่ได้ Core จะถือ state ที่ไม่มี log ไหนอธิบาย
2. **One simulation, many presentations** — assembly เดียวต้องรันได้ทั้ง headless simulator (ข้อ 1) และ Unity Core ที่ไม่มีพิกัดคือสิ่งที่ทำให้เป็น *โค้ดเดียวกัน* ไม่ใช่สอง implementation
3. **Testable** — rule ที่เขียนว่า "terminal ใน slot 3 ถูกล็อก" assert ได้ตรง ๆ; rule ที่เขียนว่า "terminal ที่ x=4.2, y=-1.7 ถูกล็อก" ต้องตั้ง spatial setup ก่อนจะพูดอะไรได้เลย

## Enforcement — ตาราง naming ที่เป็นส่วนหนึ่งของ contract

`CorePurityTests` ปฏิเสธพิกัดและ render-shaped types บน public surface ของ Core และ convention ด้านล่างคือส่วนหนึ่งของสัญญา:

> **ยืนยันแล้วว่ามีกลไก:** `CorePurityTests` **มีอยู่จริง** และตรวจ**ระดับชื่อด้วย** ไม่ใช่แค่ type: *"A field called `GridX` passes every type-level check — the type is an innocent `int` — so the rule has to be enforced on names as well as types"* ดู [[systems/core-purity-tests]] และ [[systems/grid-to-slot-migration]]

| แนวคิด | ชื่อที่ใช้ใน Core | ห้าม |
|---|---|---|
| ของอยู่ที่ไหน | `SlotIndex`, `Layer` | `x`, `y`, `z`, `position`, `transform` |
| ขนาดเท่าไร | `SlotCount`, `Tier` | `width`, `height`, `scale`, `bounds` |
| หน้าตาเป็นยังไง | `TypeId`, `NameKey` | `mesh`, `sprite`, `prefab`, `material` |
| ใครมองเห็น | `Revealed` | `visible`, `rendered`, `occluded` |

## การย้ายจริง: grid → slot

`ARCHITECTURE.md` บันทึกการเปลี่ยนแปลงที่ทำให้กฎนี้เป็นจริง:

| Was | Is |
|---|---|
| `GridX`, `GridY`, `Width` | `SlotIndices` (a set), `Layer` |
| adjacency derived by comparing coordinates | `AdjacentRoomIds`, **explicit และ symmetric** |
| `RoomAt(x, y)` | `RoomInSlot(layer, slot)` |
| `RoomsAtDepth(y)` | `RoomsAtLayer(layer)` |
| `PlacementError.OutOfBounds` / `OverlapsExisting` / `NonPositiveWidth` | `LayerOutOfRange` / `SlotOccupied` / `NoSlots` |

**สองอย่างที่รักษาไว้โดยเจตนา** เพราะเป็นกฎ: **Layer** (depth เป็น rule input — `ApplyDepthCost`) และ **merge-adjacency** (ห้องต้องแชร์ layer กัน)

**ผลข้างเคียงที่ต้องพูดตรงๆ:** adjacency เป็น data แล้ว **ระยะจึงไม่ได้แปลว่าติดกัน** ห้องสองห้องอาจอยู่ข้างกันเชิงตัวเลขแต่ไม่ touch เพราะ Presentation เป็นผู้ตัดสิน สิ่งที่อยากได้พฤติกรรม grid ต้อง**ประกาศให้ชัดเจน** ดู [[systems/grid-to-slot-migration]]

## เชื่อมโยง

- [[determinism]] — เหตุผลข้อ 1 ของข้อนี้คือข้อ 6
- [[core-purity]] — enforcement อีกชั้นของหลักการเดียวกันบน assembly ระดับ type
- [[command-log]] — บังคับว่าการเดินต้องเป็น command จึง replay ได้
- [[reason-code]] — `NameKey` ในตารางข้างบนคือฝั่ง key ของแนวคิดเดียวกับการไม่ส่ง prose
- fog of war (Stage 4, ยังไม่มีหน้า) — `Revealed` ในตารางข้างบนคือช่องว่างที่ fog of war ควรเติม
- [[systems/grid-to-slot-migration]] — การถอดพิกัดออกจริง พร้อมตาราง Was/Is
- [[systems/core-purity-tests]] — กลไกตรวจทั้ง type และชื่อ
- [[systems/grid-commands]] — สะพานแปลงสำหรับ test และ UI
- [[systems/command-log]] — เหตุผลว่าทำไม "เดิน" ต้องเป็น command
- [[design/base-building]] — กฎ merge และ depth cost ที่รักษาไว้
- [[design/art-direction]] — 96×64 px ต่อช่องห้องเป็นเรื่อง presentation
- [[sources/knowledge]] — กฎข้อ 10

## คำถามที่ยังไม่มีคำตอบ

- [ ] `SlotCount` กับ `Tier` แยกจากกันอย่างไร — ทั้งคู่อยู่ในแถว "ขนาดเท่าไร" แต่ดูเหมือนคนละแนวคิด
- [ ] ~~adjacency เก็บเป็น data รูปแบบไหน~~ — **ตอบแล้ว:** `AdjacentRoomIds` เป็น explicit และ symmetric ดู [[systems/grid-to-slot-migration]]
- [ ] symmetric adjacency ถูกบังคับใช้ด้วย test หรือเป็นแค่วินัย
- [ ] fog of war (Stage 4) จะใช้ `Revealed` อย่างไร และเป็น state ระดับ agent, room หรือ interactable — `GDD.md` ให้สถานะ fog สี่ระดับ (Hidden/Silhouette/Scouted/Revealed) ที่ผูกกับ `Infiltration` แต่ยังไม่ระบุว่าเก็บที่ไหน ดู [[design/mission-and-fog-of-war]]
