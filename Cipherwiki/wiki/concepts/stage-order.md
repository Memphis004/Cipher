---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, project-shape, stages]
---

# Project Shape และลำดับ Stage

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 1)
> **อัปเดต:** 2026-10-02

เกมเป็น **single-player Steam game** แนว **simulation-first**: เกมต้องเล่นสนุกแบบ headless ใน terminal ก่อน จะใช้เวลาไปกับ art หรือ UI แค่หนึ่งชั่วโมง (Stage 6)

## ลำดับ Stage

Stage 1 → 6 เป็นลำดับตายตัว **ห้ามสลับ** Stage 7–10 ค่อนข้างอิสระต่อกัน แต่ **8 ต้องมาก่อน 9**

| Stage | ขอบเขต | สถานะ |
|---|---|---|
| 1 | Solution skeleton + domain model | done |
| 2 | Data tables (Luban) | done |
| 3 | Simulation core: agents, rooms, economy | done |
| 4 | Mission generation + fog of war | not started |
| 5 | Save/load, replay, determinism | not started |
| 6 | Headless playability + balance harness | not started |
| 7 | Unity bootstrap + editor automation | not started |
| 8 | UI framework + Base scene | not started |
| 9 | Mission scene + game feel | not started |
| 10 | Meta, Steam, polish, release | not started |

> **ยืนยันแล้ว:** ตารางสถานะข้างบนมาจาก `raw/README.md` ส่วน "Stage status" ซึ่ง ingest แล้ว — ไม่ใช่การอนุมานอีกต่อไป ดู [[sources/README]]

## ProjectSpy.Core

`ProjectSpy.Core` เป็น **pure simulation library** target `netstandard2.1` เพื่อให้ assembly เดียวกันรันได้ทั้งใน Unity, ใน console simulator และใน tests

## เชื่อมโยง

- [[core-purity]] — เหตุผลเชิงโครงสร้างของการเลือก netstandard2.1
- [[magic-numbers]] — Stage 2 คือจุดที่กฎ "ไม่มี magic number" เริ่มมีผล
- [[determinism]] — Stage 5 คือ save/load, replay, determinism
- [[slot-layer-abstraction]] — ข้อ 1 เป็นเหตุผลข้อ 2 ของข้อ 10 (one simulation, many presentations)
- [[sources/knowledge]] — กฎข้อ 1
- [[project-layout]] — โครงสร้างไฟล์และ build
- [[design/project-vision]] — เนื้อหาโครงการที่ stage ต่างๆ จะส่งมอบ

## คำถามที่ยังไม่มีคำตอบ

- [ ] "loans and a bankruptcy ladder" — ขั้นบันไดเป็นอย่างไร ต่างจาก Heat ที่ศัตรูบุกฐานอย่างไร
- [ ] "loyalty drift and its escalation ladder" — drift เกิดจากอะไร และ ladder มีกี่ขั้น
- [ ] Act ของ [[design/progression]] (Act 1 = วัน 1-30) สัมพันธ์กับ Stage ของโปรเจกต์อย่างไร หรือเป็นคนละแกนกัน
- [ ] `CorePurityTests` มีแล้ว — grep audit ใน Stage 5 item 6 จะตรวจอะไรเพิ่มจาก test

## ข้อสังเกต

`knowledge.md` ระบุว่าการมี Roslyn analyzer / `CorePurityTests` ถูกเพิ่มใน **Stage 5** และ grep ค้างอยู่ใน Stage 5 item 6 แต่ Stage 5 ยัง not started — นี่คืองานที่ผังไว้ ยังไม่มีกลไกบังคับใช้จริงในตอนนี้

> **อัปเดตจากการ ingest `ARCHITECTURE.md`:** `CorePurityTests` **มีอยู่แล้ว** และทำงานทั้ง type-level และ name-level ดู [[systems/core-purity-tests]] — ส่วนที่ยังไม่มีคือ **full grep audit และ Roslyn analyzer** ซึ่งเป็นงานของ Stage 5

## Stage 3 ส่งมอบอะไรแล้ว

จาก [[sources/README]]:

- tick pipeline หก phase, training, recovery และ burnout
- **weekly settlement with loans and a bankruptcy ladder**
- recruitment
- **loyalty drift และ escalation ladder**
- **mole with counter-intel**

Headline guarantee สองข้อ: 365-day unattended run อยู่ในกฎทางการเงินและ reproducible และสอง session ที่ใช้ seed/command log เดียวกันให้ byte-identical state ดู [[systems/determinism-tests]]

> **ช่องว่างของ wiki:** loans/bankruptcy ladder และ loyalty drift/escalation ladder ยังไม่มี design page — เป็น design content ที่ควรเพิ่ม ดูคำถามท้ายหน้า
