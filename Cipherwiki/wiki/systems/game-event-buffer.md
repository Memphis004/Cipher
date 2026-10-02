---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, events, presentation]
---

# GameEvent buffer — เหตุผลที่ event ต้องถูกบัฟเฟอร์

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

Commands และ tick phases **ไม่ publish ตรงไปยัง subscriber** — ทั้งหมด queue ลง pending buffer แล้ว `FlushEvents` ระบายตามลำดับ

```
   Execute / AdvanceTick
            │
            ▼
   _pending.Add(new RoomBuilt(...))        ← buffered
   _pending.Add(new ResourcesChanged(...))
            │
            ▼
   FlushEvents() → snapshot handlers → dispatch in order
```

## ปัญหาที่ buffer นี้แก้

> "Without the buffer, a UI handler could **re-enter `Execute` mid-command** and observe a half-mutated world — the classic Presentation bug that is miserable to reproduce because it depends on click timing"

นี่คือ **re-entrancy** — handler ที่ถูกเรียกระหว่าง command ยังไม่เสร็จ จะเห็น world ที่อยู่ในสภาวะครึ่งทาง และถ้ามันสั่ง command ซ้อน จะเกิด mutation ซ้อนซ้อนใน command เดียว ซึ่งละเมิด [[determinism]] ทันที

ปัญหานี้ reproduce ยากเพราะขึ้นกับจังหวะการคลิกของผู้เล่น — ลักษณะเดียวกับที่ [[systems/game-session]] อธิบายว่าทำไม non-determinism ถึงเป็นสิ่งที่ต้อง crash

## คุณสมบัติของ event

- **เป็น immutable records of facts** — บันทึกสิ่งที่เกิดขึ้น ไม่ใช่คำสั่ง
- **publish *หลัง* การเปลี่ยนแปลงที่มันอธิบาย**
- Presentation **render from events** — ไม่ poll Core state ใน frame loop เพื่อเดาว่าเกิดอะไรขึ้น

ข้อสุดท้ายสอดคล้องกับ [[layering]]: Core เป็นผู้บอกว่าเกิดอะไรขึ้น ไม่ใช่ให้ UI ไปค้นหาเอง

## `CommandRejected` ก็เป็น event เหมือนกัน

ดู flow ใน [[systems/game-session]] — `Rejected(reason, args)` ไม่แตะ state แต่ยัง **publish `CommandRejected`** เพื่อให้ UI รู้ว่าทำไมปุ่มถูกปฏิเสธ เป็นการแยก "ไม่สำเร็จ" ออกจาก "ไม่มีอะไรเกิดขึ้น"

## เชื่อมโยง

- [[systems/game-session]] — ต้นทางที่ queue event
- [[systems/command-log]] — command ที่ถูก log แล้วจึงผลิต event
- [[systems/tick-pipeline]] — phase ก็ queue ลง buffer เดียวกัน ไม่ publish ตรง
- [[layering]] — event คือช่องทางที่ Core สื่อสถานะโดยไม่เปิดให้ถูกแก้
- [[reason-code]] — `CommandRejected` พา args ไปให้ Presentation localize
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] `_pending` ถูกล้างที่ไหน — หลัง `FlushEvents` หรือหลัง `Execute` คืน
- [ ] Handler ที่ subscribe ระหว่างกำลัง dispatch ถูกเอาไปใน snapshot รอบถัดไปหรือไม่
- [ ] ถ้า handler สั่ง `Execute` ระหว่าง `FlushEvents` event ที่เหลือจะถูกจัดการอย่างไร
- [ ] `GameEvent` มีกี่ชนิด ณ Stage 3 และเป็น record แบบ positional หรือมี field เยอะ
