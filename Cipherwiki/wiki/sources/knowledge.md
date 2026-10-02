---
type: source
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, rules, authoritative]
---

# knowledge.md — กฎการทำงานของ Project CIPHER

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md)
> **อัปเดต:** 2026-10-02

สรุปหนึ่งย่อหน้า: `knowledge.md` คือ **กฎชุดที่บังคับใช้ (binding rules) ของทุก stage ในโครงการ** สร้างขึ้นใน Stage 1 จากบรีฟที่ให้มา ทุกบรรทัดในไฟล์นี้มีผลผูกพันกับทุก stage ที่จะมาถัดไป ถ้า brief ในอนาคตขัดกับบรรทัดใดบรรทัดหนึ่งในไฟล์นี้ **ต้องหยุดและถามก่อนเขียนโค้ด** — ไฟล์นี้ไม่ใช่เอกสารอ้างอิง แต่เป็นข้อบังคับ

กฎชุดนี้มี 10 ข้อ ครอบคลุม 4 แกนหลัก: รูปร่างโครงการ, purity ของ Core, determinism, และ data integrity

## สารบัญกฎ

| # | หัวข้อ | สาระสั้น | หน้า |
|---|---|---|---|
| 1 | Project shape | เกม single-player Steam, simulation-first, ลำดับ stage ห้ามสลับ | [[stage-order]] |
| 2 | Core must not reference UnityEngine | Core ห้ามแตะ Unity และ API ไม่ deterministic | [[core-purity]] |
| 3 | No magic numbers after Stage 2 | ตัวเลข balance ทั้งหมดอยู่ใน CSV ที่ Luban compile | [[magic-numbers]] |
| 4 | Core never produces English prose | Core คืน `ReasonCode` ไม่ใช่ประโยคภาษาอังกฤษ | [[reason-code]] |
| 5 | Out-of-scope bodies must be explicit | ข้างที่ยังไม่ทำต้อง throw พร้อม `// TODO(stage-N):` | [[out-of-scope-bodies]] |
| 6 | Determinism is a hard requirement | seed + command log เดิม ต้องได้ `WorldState` byte-identical | [[determinism]] |
| 7 | Data integrity enforced by tests | foreign key ผิดต้องทำให้ build พัง | [[data-integrity]] |
| 8 | Layering | Presentation → Core ทางเดียว, mutation ผ่าน `ICommand.Execute` เท่านั้น | [[layering]] |
| 9 | Scope discipline | เปลี่ยนน้อยที่สุดที่ถูกต้อง, รายงานตรงไปตรงมา | [[scope-discipline]] |
| 10 | Core is presentation-agnostic | Core ไม่เก็บพิกัด, mesh, camera — ใช้ slot/layer แทน | [[slot-layer-abstraction]] |

## กฎที่มีรายละเอียดเชิงเหตุผล

ข้อ 10 มีส่วน `### Why` ที่ให้เหตุผล 3 ข้อ เรียงตามน้ำหนัก: (1) **Determinism** — ตำแหน่งที่เปลี่ยนโดยไม่มี command ใน log จะ replay ไม่ได้ (2) **One simulation, many presentations** — assembly เดียวต้องรันได้ทั้ง headless simulator และ Unity (3) **Testable** — rule ที่เขียนว่า "terminal ใน slot 3 ล็อกอยู่" assert ได้ตรง ๆ แต่ถ้าเป็นพิกัด x=4.2, y=-1.7 ต้องตั้ง spatial setup ก่อนจะพูดอะไรได้

ข้อ 10 ยังบังคับ naming convention ผ่านตาราง (ดูที่ [[slot-layer-abstraction]])

## Enforcement

กฎส่วนใหญ่ไม่ได้พึ่งวินัย แต่พึ่ง **build ที่พัง**:

- ข้อ 2 — Roslyn analyzer หรือ reflection unit test ต้อง fail build เมื่อเจอ forbidden type (เพิ่มใน Stage 5; grep ค้างอยู่ใน Stage 5 item 6)
- ข้อ 7 — test ตรวจ foreign key, weights, localization keys และ fail build
- ข้อ 10 — `CorePurityTests` ปฏิเสธพิกัดและ type ที่มีหน้าตาเรื่อง render บน public surface ของ Core

## เชื่อมโยง

- [[determinism]] — ข้อ 6 และเหตุผลข้อ 1 ของข้อ 10 ชี้ไปทางเดียวกัน
- [[slot-layer-abstraction]] — ข้อ 10 ใช้เหตุผลของข้อ 6 เป็นข้อโต้แย้งหลัก
- [[command-log]] — ข้อ 6 และ 8 ต้องมี `CommandLog` จึงจะ replay ได้

## หมายเหตุ

- ไฟล์นี้เป็นกฎระดับ project ที่ `AGENTS.md` §2.2 ประกาศให้ authoritative — ต่ำกว่า wiki และต่ำกว่า schema
- Stage 3 ส่งมอบแล้ว ข้อ 3 (no magic numbers) มีผลบังคับใช้**ตั้งแต่ตอนนี้** ไม่ใช่ตอน Stage 2
- `README.md` ระบุว่า `docs/` มีโครงสร้างคล้ายกัน แต่ยังไม่ได้ ingest
