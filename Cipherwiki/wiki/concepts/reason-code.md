---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, core, localization, presentation]
---

# ReasonCode — Core ไม่ผลิต prose ให้ผู้เล่น

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 4)
> **อัปเดต:** 2026-10-02

**Core ไม่เคยผลิต English prose ให้ผู้เล่นอ่าน** คำสั่งที่ถูกปฏิเสธจะคืนค่า **`ReasonCode` enum + args** แล้วให้ **Presentation layer** เป็นผู้ map code นั้นเป็นข้อความที่ localize แล้ว

กฎเดียวกันนี้ใช้กับ "อย่างอื่นที่ผู้เล่นอ่าน" ทั้งหมด: **Core คืน keys, ids และ structured values เท่านั้น ไม่ใช่ประโยค** — ภาษาไทยเป็นค่าเริ่มต้น, `en` เป็นคอลัมน์คู่ขนาน

## ทำไมถึงเป็นกฎ

เป็นรูปแบบเดียวกับ [[slot-layer-abstraction]] ในอีกมิติหนึ่ง — แยกสิ่งที่ Core ต้องรู้ออกจากสิ่งที่ Presentation ต้องตัดสิน และทำให้แต่ละชั้นแทนที่ได้โดยไม่ต้องแตะ Core

> **ชื่อ type ที่ยืนยันแล้ว:** กฎข้อ 4 เขียนว่า `ReasonCode` แต่ `ARCHITECTURE.md` ระบุชัดว่า **`CommandResult` ถือ `CommandReason` enum และ `CommandArgs` struct** และ Presentation มองหา `command.reject.<reason>` ในตาราง localize — น่าจะเป็นชื่อที่แม่นยำกว่า ดู [[systems/game-session]]

## จุดที่กฎนี้มีผลกับระบบอื่น

| ตัวอย่าง | ผลกระทบ |
|---|---|
| Command ที่ถูกปฏิเสธ | publish `CommandRejected` เป็น event แต่ **ไม่แตะ state** ดู [[systems/game-event-buffer]] |
| Auto-pause trigger | ต้องเป็น key/เหตุการณ์ ไม่ใช่ข้อความ ดู [[design/time-and-calendar]] |
| Loyalty ที่ซ่อนจากผู้เล่น | Presentation ตัดสินใจไม่แสดงตัวเลข ดู [[design/agents]] |
| Ending ตาม Loyalty/Heat/Intel | Core คำนวณค่า ผู้เล่นเห็นผลลัพธ์ ดู [[design/progression]] |

## Validation

`TableValidator` ตรวจ **missing localization key** และตรวจว่า **ทุก key ที่ระบบอ่านต้องมีอยู่** เพื่อไม่ให้ typo กลายเป็น fallback ที่ลบ penalty อย่างเงียบงัน ดู [[systems/table-validator]]

## เชื่อมโยง

- [[slot-layer-abstraction]] — `NameKey` ในตาราง naming convention เป็นฝั่ง key ของแนวคิดเดียวกัน
- [[layering]] — Presentation เป็นเจ้าของ input, rendering, audio และ localization
- [[data-integrity]] — localization key ที่หายไปต้องทำให้ build พัง
- [[magic-numbers]] — key และข้อมูล localize เป็นข้อมูล tuneable ชิ้นหนึ่งที่อยู่ใน CSV
- [[sources/knowledge]] — กฎข้อ 4
- [[systems/game-session]] — `CommandResult`, `CommandReason`, `CommandArgs`

## คำถามที่ยังไม่มีคำตอบ

- [ ] `CommandReason` มีกี่ค่า และ `CommandArgs` เป็น struct ชนิดไหน
- [ ] เคสไหนบ้างที่ Core ต้องคืน key แต่ไม่ใช่ `CommandReason` (เช่น ชื่อสิ่งของที่ยังไม่เปิดเผย)
- [ ] fallback เมื่อ key หายคืออะไร — `TableValidator` ตรวจ missing key แล้ว แต่ตอน runtime ถ้าโหลดตารางไม่ได้จะเป็นอะไร
- [ ] ตาราง localize อยู่ใน CSV ชุดเดียวกับ balance หรือแยก
