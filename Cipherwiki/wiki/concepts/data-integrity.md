---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, testing, data, save]
---

# Data Integrity บังคับใช้ด้วย test ไม่ใช่ด้วยวินัย

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 7)
> **อัปเดต:** 2026-10-02

Foreign keys, weights และ localization keys ถูกตรวจโดย **test ที่ fail build เมื่อ designer แก้ CSV พลาด** — ตารางที่พัง **ต้องไม่มีวันไปถึงสถานะที่เล่นได้**

## Save integrity

การโหลด save ที่มี **id ซึ่งไม่มีอยู่แล้ว** ต้อง **quarantine ลงใน integrity report** — ไม่ crash และไม่ drop ทิ้งเงียบ ๆ

สองอย่างนี้เป็นคนละเรื่อง: test กันไม่ให้ตารางพังตั้งแต่ต้น แต่ quarantine กันไม่ให้ save เก่าที่มี id ซึ่งหายไปพังตอนโหลด — เพราะ save เก่าจะเสียเปรียบตลอดไปเมื่อ designer ลบ content

> **เหตุผลที่สองเส้นทางนี้ถูกแยกออกจากกันโดยเจตนา:** `TableValidator` ทำให้ **missing data เป็น build failure** แต่ accessor (`SimulationRules`) กลับ **คืนค่า default ที่ document ไว้แทนการ throw** เพราะ Stage 5 "deliberately loads saves that may reference retired ids" ดู [[systems/simulation-rules]]

> **สถานะปัจจุบัน: ยังไม่มี save file จริง** `WorldStateSerializer` เป็นแค่ *fingerprint-grade canonical dump* สำหรับเทียบ byte ไม่ใช่ save format — save format เป็นงานของ Stage 5 ดู [[systems/world-state]] และ [[systems/tech-stack]]

## เชื่อมโยง

- [[magic-numbers]] — CSV คือที่มาของ foreign key, weights และ localization keys
- [[reason-code]] — localization keys ที่ validate ในกฎนี้คือฝั่ง key ที่ Core คืน
- [[determinism]] — quarantine เป็นการรายงาน ไม่ใช่การแก้ state ซึ่งต้องไม่ทำให้ replay เพี้ยน
- [[command-log]] — ids ใน save ต้องตรงกับที่ command log บันทึกไว้
- [[scope-discipline]] — กฎข้อ 9
- [[sources/knowledge]] — กฎข้อ 7

## กฎของ `TableValidator` ที่เป็นเรื่อง data integrity

นอกจาก foreign key/weight/localization key แล้ว ยังมีกฎที่จับปัญหาแบบ**ที่ตารางดูปกติแต่ระบบทำงานเงียบ ๆ**:

- **`ValidateCounterIntelBalance`** — เคสต้องสรุปได้ใน best case **และไม่สรุปโดยเฉลี่ย** มิฉะนั้น counter-intel จะเปิดเผยคนมั่ว ๆ
- **Required keys per rule table** — กัน typo ที่กลายเป็น fallback ซึ่งลบ penalty เงียบ ๆ
- **Id space disjointness** — แต่ละตารางมี id band ของตัวเอง

ดู [[systems/table-validator]] และ [[systems/mole-system]]

## คำถามที่ยังไม่มีคำตอบ

- [ ] integrity report ถูกเขียนลงไหน และมี UI ให้ผู้เล่นดูหรือไม่
- [ ] quarantine แล้ว state ของโลกถูกกำหนดเป็นอย่างไร — โหลดต่อโดยไม่มี entity หรือหยุด
- [ ] มีเส้นทางสำรอง (fallback) สำหรับ id ที่ถูก quarantine เช่น กลับไปใช้ค่า default หรือไม่
- [ ] `docs/SAVE-FORMAT.md` ถูกอ้างถึงใน README แต่ไม่มีไฟล์ — จะเขียนเมื่อไร ดู [[sources/README]] และ [[project-layout]]
- [ ] `migration step` แบบมีเลข + test ต่อ step จะใช้ schema version จาก save header อย่างไร — ยังไม่ปรากฏในเอกสารที่ ingest แล้ว
