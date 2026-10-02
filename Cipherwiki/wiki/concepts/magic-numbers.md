---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, tables, luban, balance]
---

# No magic numbers in Core

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 3)
> **อัปเดต:** 2026-10-02

ทุกค่าที่ปรับได้ (tuneable) อยู่ใน **CSV table** ที่ **Luban** compile เป็น `ProjectSpy.Tables` **ตั้งแต่ Stage 2 ลงมา Core ไม่มีตัวเลข balance ที่พิมพ์มืออีกแล้ว** — ไม่มี rate, ไม่มี cost, ไม่มี threshold, ไม่มี multiplier

## ข้อยกเว้น: structural constants

ค่าที่เป็น **โครงสร้าง ไม่ใช่ tuneable** อนุญาตให้อยู่ใน Core และต้องถูก **document ไว้ว่าเป็นโครงสร้าง** เช่น

- ticks per day
- grid width

เส้นแบ่ง: ถ้าตัวเลขนั้นควรเปลี่ยนตอนปรับ balance → ต้องอยู่ใน CSV ถ้าเปลี่ยนแล้วอะไรพัง (เช่น logic หรือ save format) → เป็นโครงสร้าง

## เหตุผลที่เป็น determinism rule ทางอ้อม

ตัวเลขที่พิมพ์มือใน source เป็นค่าที่ designer แก้ไม่ได้และไม่มีใครเห็นว่ามันอยู่ตรงไหน — ซึ่งเป็นเหตุผลว่าทำไมกฎนี้ถูกบังคับใช้ *หลัง* Stage 2 ทันที ไม่ใช่รอ Stage ถัดไป

> **ยืนยันแล้ว:** `ARCHITECTURE.md` ยืนยันตรง ๆ ว่า *"Since stage 2 no tuneable value is hard-coded in Core"* และอธิบายกลไก: ทุกอย่างอยู่ใน `data/*.csv` → Luban → `ProjectSpy.Tables` → อ่านผ่าน `RoomDefinitions` และ `SimulationRules` ดู [[systems/simulation-rules]]

## กลไกที่ทำให้กฎนี้ไม่ใช่แค่คำสัญ

| กลไก | หน้าที่ |
|---|---|
| `SimulationRules` | ให้ rule system ขอเป็น **ชื่อ** ไม่ใช่ column index — มีที่เดียวที่ต้องดูเมื่อ designer ถาม "33 มาจากไหน" |
| `AreTablesLoaded` | ให้ test แยก "ได้ fallback" ออกจาก "ได้ข้อมูลจริง" — **ทำให้ fallback ซื่อสัตย์** |
| `TableValidator` | catch ข้อมูลที่พังตั้งแต่ build time ดู [[systems/table-validator]] |

> "**Missing data is a build failure (`TableValidator`), not a runtime crash.**"

accessor ต้อง *tolerant* เพราะ Stage 5 จงใจโหลด save ที่อาจอ้างถึง id ที่ถูกถอด — ดู [[data-integrity]]

## เชื่อมโยง

- [[data-integrity]] — CSV ผิดต้องทำให้ build พัง ไม่ใช่เรื่องความระเบียบวินัย
- [[reason-code]] — localization key เป็นข้อมูล tuneable ชิ้นหนึ่งในชุด CSV เดียวกัน
- [[stage-order]] — Stage 2 คือจุดเริ่มมีผลบังคับใช้ของกฎนี้
- [[determinism]] — ค่าในตารางต้องเป็น integer หรือ fixed-point ใน rule paths
- [[core-purity]] — Core ยังคงเป็น netstandard2.1 ที่ไม่มี Unity reference แม้จะอ่านจาก `ProjectSpy.Tables`
- [[sources/knowledge]] — กฎข้อ 3

## ข้อสังเกต

> **ตอบแล้ว:** `knowledge.md` ไม่ได้ระบุว่า `ProjectSpy.Core` อ้าง `ProjectSpy.Tables` อย่างไร — จากการ ingest [[sources/README]] และ [[sources/ARCHITECTURE]] ยืนยันว่าเป็น **direct project reference** และอนุญาต เพราะ tables assembly เป็น pure compiled data ไม่มี Unity type และ target netstandard2.1 เหมือนกัน

> **ตัวเลขใน GDD เป็น design intent ไม่ใช่ค่าที่อยู่ในโค้ด:** `Infiltration/25`, Alarm 0-100, `Mental dmg -30%`, Act 1 = วัน 1-30, 48 สี, 96×64 px — ตัวเลขเหล่านี้อยู่ในเอกสาร design ดู [[design/mission-and-fog-of-war]] และ [[design/agents]] ส่วน 48 สีกับ 96×64 px เป็นเรื่อง presentation อยู่แล้ว ดู [[design/art-direction]]

## คำถามที่ยังไม่มีคำตอบ

- [ ] มี "structural constants" ที่ document ไว้แล้วกี่ตัว และอยู่ที่ไหน — `GameClock` ใช้ 24/168 ดู [[systems/game-clock]]
- [ ] `SimulationRules` มีกี่กฎ และ id band แต่ละตารางคือช่วงไหน ดู [[systems/simulation-rules]]
