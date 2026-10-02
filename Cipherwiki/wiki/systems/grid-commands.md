---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, testing, grid, translation]
---

# GridCommands — สะพานแปลง grid สำหรับ test และ UI

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

Test code ที่อยากคิดเป็น grid เรียก **`GridCommands.Build`** และ **`World.Place`** ซึ่งแปลง grid triple เป็น slots, layer และ adjacency ที่ derive แล้ว

> "**the same translation a real floorplan UI performs**"

## ทำไมต้องมี

การถอดพิกัดออกจาก Core ([[systems/grid-to-slot-migration]]) เปลี่ยน signature ของ `BuildRoomCommand` และทำให้ call site เดิมพังทั้งหมด ตัวแปลงนี้ทำให้:

- **~115 call sites ไม่ต้องแก้** ขณะที่ Core ยังคงไม่มีพิกัด
- test เขียนได้ในภาษาของ grid ซึ่งเข้าใจง่ายกว่า slot arithmetic
- floorplan UI ใช้การแปลงเดียวกัน → **test กับ UI ทดสอบ path เดียวกัน**

## ไม่ใช่ทางลัดข้ามกฎ

ข้อนี้สำคัญ: `GridCommands` **ไม่ได้ทำให้ Core มีพิกัดอีก** — พิกัดอยู่ใน **ชั้นที่แปลง** และผลลัพธ์ที่เข้า Core เป็น slot/layer/adjacency ตามกฎข้อ 10 ([[slot-layer-abstraction]])

`CorePurityTests` ยังคงทำงานต่อได้เพราะ member ที่มีรูปพิกัดของ Core ไม่เพิ่มขึ้น ดู [[systems/core-purity-tests]]

## เป็นตัวอย่างของ scope discipline

> "That kept **~115 call sites** unchanged while Core stayed coordinate-free"

การเปลี่ยนโครงสร้างขนาดใหญ่โดย**ไม่แตะ call site ที่ไม่เกี่ยวข้อง** คือ [[scope-discipline]] ที่ปฏิบัติจริง — กฎบอกให้ "ห้าม refactor code นอก blast radius" และหน้านี้คือวิธีที่ทำได้

## เชื่อมโยง

- [[systems/grid-to-slot-migration]] — การเปลี่ยนแปลงที่ shim นี้รองรับ
- [[slot-layer-abstraction]] — กฎข้อ 10 ที่ shim นี้รักษาไว้
- [[systems/core-purity-tests]] — ยังจับการย้อนกลับไปใช้พิกัดได้
- [[systems/game-session]] — `BuildRoomCommand` ที่ shim สร้าง
- [[systems/command-log]] — ทุกอย่างที่ผ่าน shim ยังต้องเป็น command
- `raw/doc/FLOORPLAN_UI.md` — ยังไม่ได้ ingest: contract ของ slot ↔ screen mapping
- [[scope-discipline]] — หลักการที่ shim นี้เป็นตัวอย่าง
- [[design/base-building]] — merge adjacency ที่ shim ต้อง derive ให้ถูก
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] `GridCommands.Build` อยู่ใน Core หรือใน test assembly — ถ้าอยู่ใน Core แปลว่ามี grid type ใน Core ซึ่งขัดกับกฎข้อ 10 หรือไม่
- [ ] ~115 call sites นับเฉพาะ production หรือรวม test
- [ ] `World.Place` ต่างจาก `GridCommands.Build` อย่างไร — ดูเหมือนเป็นสองทางเข้าของการวางห้อง
- [ ] การ derive adjacency จาก grid ต่างจากการ derive ของ floorplan UI อย่างไร ถ้า UI ใช้การคลิกลาก
