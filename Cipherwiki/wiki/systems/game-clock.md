---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, time, clock]
---

# GameClock — เจ้าของเวลา

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

`GameClock` ถือ **tick เดียว** ตัวแปรชนิด `long` ที่ granularity รายชั่วโมง: **24 ticks ต่อวัน, 168 ต่อสัปดาห์** มัน raise `OnTick`, `OnDayChanged`, `OnWeekChanged` และ **ไม่รู้ว่า tick หนึ่งหมายถึงอะไรสำหรับโลก**

## แยกเวลาออกจากความหมาย

การที่ `GameClock` "knows nothing about what a tick means" คือเส้นแบ่งที่ชัดเจน — ตรรกะว่า tick หนึ่งทำอะไรอยู่ใน `TickPipeline` (ดู [[systems/tick-pipeline]]) ไม่ใช่ในนาฬิกา

นี่ทำให้ clock ทดสอบได้ง่าย (เลื่อนเวลาได้โดยไม่ต้อง simulate) และทำให้ pipeline ทดสอบได้โดยการเรียกตรง

## ตัวเลขเป็น structural constant

`24 ticks/day` และ `168/week` เป็นค่าที่อยู่ใน Core ได้ตาม [[magic-numbers]] เพราะเป็น **โครงสร้าง ไม่ใช่ tuneable** — เวลาเกมไม่ควรถูก designer ปรับจาก CSV

ตรงนี้ `168` เป็นค่าที่ *derive* มาจาก `24 × 7` ไม่ใช่ค่าที่พิมพ์มือแยก ถ้ามีอยู่ในโค้ดจริงควรเป็นการคำนวณ ไม่ใช่ตัวเลขตัวที่สอง

## ความเร็วเกมไม่กระทบผลลัพธ์

> "The player may pause and run at 1x / 2x / 4x. Speed affects only how fast **real seconds map to ticks**; it **NEVER changes simulation results**."

ข้อนี้มาจากสำเนา `knowledge.md` ที่ฝังใน [[sources/GDD]] และเป็น **เงื่อนไขที่จำเป็นต่อ [[determinism]]**: ถ้าความเร็วเกมเปลี่ยนลำดับการ execute เมื่อเทียบกับ tick count เกมจะเล่นได้คนละแบบ

Auto-pause triggers **ต้องไม่ถูกข้ามอย่างเงียบงัน** — เป็นเงื่อนไขเดียวกัน: ถ้าข้ามไปผู้เล่นจะเห็นผลที่ต่างจากที่ตั้งใจ

## เชื่อมโยง

- [[systems/tick-pipeline]] — ลำดับ phase ที่ `AdvanceTick()` ขับเคลื่อน
- [[systems/tick-order-tests]] — `TickOrderTests` ที่ assert ลำดับ
- [[design/time-and-calendar]] — design intent ของระบบเวลา
- [[design/agents]] — training/recovery กินเวลาเป็น tick
- [[determinism]] — เหตุผลที่ time เป็น tick-based
- [[magic-numbers]] — ข้อยกเว้นของ structural constants
- [[systems/simulation-rules]] — ค่า recovery ratio ที่อ่านจากตาราง
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] Tick เริ่มที่เท่าไร และ campaign Act 1 (วัน 1) เริ่มที่ tick เท่าไร
- [ ] `OnDayChanged` กับ `OnWeekChanged` ถูก raise ก่อนหรือหลัง `TickPipeline` ของ tick นั้น — สำคัญเพราะ settlement ต้องเห็น state หลัง phase ทั้งหมด
- [ ] Auto-pause trigger ถูก wire เข้ากับ clock อย่างไร และเกิดก่อนหรือหลัง flush event
