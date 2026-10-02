---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, time, calendar]
---

# Time & Calendar — ระบบเวลาแบบ tick-based

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.4)
> **อัปเดต:** 2026-10-02

| กลไก | รายละเอียด |
|---|---|
| หน่วยเวลา | **Tick** (1 tick = 1 ชั่วโมงในเกม), 24 tick = 1 วัน |
| ความเร็ว | Pause / 1x / 2x / 4x — กดหยุดได้ตลอด (single-player ทำได้เต็มที่) |
| Auto-pause | ตั้งค่าได้ว่าจะหยุดอัตโนมัติเมื่อ: ภารกิจจบ, สปายบาดเจ็บ, เงินติดลบ, มี event ใหม่ |
| สัปดาห์ | 7 วัน = 1 สัปดาห์ → หักเงินเดือน + ค่า upkeep (**จุดกดดันทางการเงินหลัก**) |
| ฤดูกาล/ช่วง | 4 Act ตามเนื้อเรื่อง, แต่ละ Act ปลดล็อกศัตรูและห้องใหม่ |

## เหตุผลที่เลือก tick-based ไม่ใช่ real-time ต่อเนื่อง

> ทำให้ทุกอย่างเป็น integer, deterministic, เซฟ/โหลดง่าย, ทดสอบอัตโนมัติได้ และ balance sim รันได้เร็วมาก

เหตุผลทั้งห้าข้อนี้คือ [[determinism]] และ [[stage-order]] พูดกันคนละภาษา: tick model คือสิ่งที่ทำให้ข้อกฎข้อ 6 ทำงานได้จริงในทางปฏิบัติ — ถ้าเวลาเป็น float วินาทีจริง เกมจะไม่ deterministic และ Stage 6 balance harness จะใช้ไม่ได้

## เชื่อมโยง

- [[systems/tick-pipeline]] — `GameClock` และ `TickPipeline` ที่ implement กลไกนี้ใน Core
- [[systems/tick-order-tests]] — ลำดับ phase ที่ทำให้ settlement ทำงานถูกต้อง
- [[determinism]] — เหตุผลเชิงระบบของ tick-based
- [[design/base-building]] — upkeep รายสัปดาห์ที่เป็นจุดกดดันหลัก
- [[design/agents]] — training และ recovery ที่กินเวลาเป็น tick
- [[design/progression]] — 4 Act ที่แบ่งตามวัน
- [[magic-numbers]] — 24 ticks/day และ 7 days/week เป็น *structural constant* ที่อนุญาตให้อยู่ใน Core ดู [[systems/game-clock]]
- [[design/core-loop]] — day cycle ใน loop หลัก

## ข้อสังเกต

กฎข้อ 4 ของ `raw/knowledge.md` ห้าม Core ผลิต prose ให้ผู้เล่น — **auto-pause trigger** จึงต้องเป็น key/เหตุการณ์ ไม่ใช่ข้อความ ดู [[reason-code]]

## คำถามที่ยังไม่มีคำตอบ

- [ ] Auto-pause 4 เงื่อนไขนี้ implement แล้วหรือยัง และใช้ key อะไร
- [ ] Act boundary ตัดสินจากวันตามลำดับ หรือจากเงื่อนไขเป็นเหตุการณ์
