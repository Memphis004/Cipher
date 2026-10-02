---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, testing, tick, determinism]
---

# TickOrderTests — การปกป้อง save contract

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

`TickOrderTests` assert สามอย่าง:

1. **ลำดับลำดับ phase**
2. **ลำดับไม่ขึ้นกับ registration order**
3. **แต่ละ pairwise relationship แยกกัน**

## ข้อ 3 คือเหตุผลที่ตาราง constraint มีอยู่

> "so a future reorder fails as a **named constraint** rather than as an unexplained simulation change"

ถ้า test แค่ assert ลำดับรวม การสลับสอง phase จะทำให้ test พังด้วยข้อความว่า "order mismatch" ซึ่งไม่บอกว่าอะไรพัง การ assert **ทีละคู่** ทำให้ test บอกได้ว่าละเมิดข้อไหน — ซึ่งเป็นความต่างระหว่าง test ที่ช่วยแก้กับ test ที่แค่บอกว่าพัง

ตาราง constraint ทั้งหมดและเหตุผลที่สลับแล้วอะไรพัง อยู่ที่ [[systems/tick-pipeline]]

## ข้อ 2 ป้องกัน regression ของกลไกเอง

Phase ถูกเก็บใน dictionary แล้วรันตาม enum order เพื่อให้ "a phase registered later cannot accidentally run in the wrong slot" — ข้อ 2 ของ test คือหลักประกันว่ากลไกนั้นยังทำงาน

## ความสัมพันธ์กับ test อื่น

| Test | ป้องกัน |
|---|---|
| `TickOrderTests` | ลำดับ phase และ dependency ระหว่าง phase |
| [[systems/determinism-tests]] | ผลลัพธ์ byte-identical ของทั้ง run |
| [[systems/core-purity-tests]] | type และชื่อที่ต้องห้ามบน Core |
| [[systems/table-validator]] | ความถูกต้องของข้อมูลตาราง |

ทั้งหมดเป็น enforcement ของกฎใน [[sources/knowledge]] ตามหลักข้อ 7: "Data integrity is enforced by tests, not by discipline"

## เชื่อมโยง

- [[systems/tick-pipeline]] — pipeline ที่ test นี้ปกป้อง
- [[systems/game-clock]] — `AdvanceTick()` ที่เรียก pipeline
- [[systems/replay]] — ทำไมลำดับถึงเป็น save contract
- [[determinism]] — กฎข้อ 6
- [[systems/determinism-tests]] — test ชุดอื่นของ determinism
- [[systems/world-state]] — `LastSettledWeek` ที่อยู่ใน phase `EventChecks`
- [[data-integrity]] — หลักการบังคับใช้ด้วย test
- [[sources/ARCHITECTURE]] — หน้าต้นทาง
