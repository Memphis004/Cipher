---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, core, purity, determinism]
---

# Core Purity — Core ห้าม reference UnityEngine

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 2)
> **อัปเดต:** 2026-10-02

`ProjectSpy.Core` เป็น simulation library ล้วนที่ target `netstandard2.1` เพื่อให้ assembly เดียวกันรันได้ทั้งใน Unity, ใน console simulator และใน xUnit **Core ห้าม reference `UnityEngine` หรือ Unity type ใด ๆ** — ไม่มี `MonoBehaviour`, ไม่มี `Vector3`, ไม่มี `Debug`, ไม่มี `Time`, ไม่มีการ serialize ผ่าน Unity's serializer

## รายการต้องห้าม

Core เป็น C# ธรรมดา: structs, classes, records, arrays และ**ห้าม reference**:

- `UnityEngine` และ Unity type ทุกชนิด
- `System.Random`
- `DateTime`
- `Guid.NewGuid`
- `File.`
- `Directory.`
- `Environment.`
- non-deterministic floating point ใน **rule paths** (ดู [[determinism]])

เหตุผลรวมกัน: ทุกอย่างในรายการนี้เป็นแหล่งที่ทำให้ผลลัพธ์ไม่ deterministic หรือผูกกับ platform — ซึ่งคือสิ่งที่ [[determinism]] ห้าม

## ทิศทาง dependency

One-way: **Presentation → Core** เท่านั้น Core ไม่มีวันเรียกกลับเข้า Presentation ดู [[layering]]

## Enforcement

Roslyn analyzer หรือ reflection-based unit test ต่อ Core assembly **ต้อง fail build** ถ้ามี forbidden type โผล่ขึ้นมา — เพิ่มใน Stage 5 และ **grep ยังค้างอยู่ใน Stage 5 item 6**

> **อัปเดตจากการ ingest `ARCHITECTURE.md`:** `CorePurityTests` **มีอยู่แล้วและทำงานแล้ว** — มันสะท้อน assembly ที่ compile แล้วและ fail ถ้ามี type ต้องห้ามบน public surface หรือใน reference table ดู [[systems/core-purity-tests]] ส่วนที่ยังไม่มีคือ **full grep audit และ Roslyn analyzer** ซึ่งเป็นงานของ Stage 5

`CorePurityTests` เป็นตัวบังคับใช้ฝั่งเดียวกัน แต่ดูกว้างกว่า: มันปฏิเสธพิกัดและ render-shaped types บน **public surface** ของ Core (ดู [[slot-layer-abstraction]])

## ข้อจำกัดเพิ่มเติมจากเอกสาร

> "Three assemblies load the same `ProjectSpy.Core.dll`: Unity, the console simulator and the test runner. That is the reason Core targets `netstandard2.1` and **carries no third-party dependencies**"

Core จึง **ต้องไม่มี NuGet dependency ใดๆ** — ถ้าการทำงานของ simulator ต่างจากเกม balance harness ของ Stage 6 จะไม่มีค่า

> **หมายเหตุ:** Core อ้าง `ProjectSpy.Tables` โดยตรง ซึ่งเป็น first-party assembly ที่ target netstandard2.1 เหมือนกัน จึงไม่ขัดข้อนี้ ดู [[systems/simulation-rules]]

## เชื่อมโยง

- [[determinism]] — เหตุผลเชิงระบบของข้อห้ามเรื่อง random/date/file
- [[layering]] — ทิศทาง dependency ที่ Core ต้องยึด
- [[stage-order]] — `netstandard2.1` เป็นส่วนหนึ่งของรูปร่างโครงการ
- [[slot-layer-abstraction]] — enforcement อีกชั้นของ Core purity
- [[sources/knowledge]] — ตารางกฎทั้ง 10 ข้อ

## คำถามที่ยังไม่มีคำตอบ

- [ ] Stage 5 item 6 (grep due) — ค้นหาอะไร และจะอยู่ใน test ตัวไหน
- [ ] "non-deterministic floating point ใน rule paths" นิยามขอบเขตของ *rule path* อย่างเป็นทางการคืออะไร
- [ ] **unordered `Dictionary`/`HashSet` enumeration** — `GDD.md` ห้ามไว้แต่ **ไม่มีใน `raw/knowledge.md`** และ `CorePurityTests` ไม่จับ (เพราะมันเป็น *behavior* ไม่ใช่ type หรือชื่อ) ดู [[systems/core-purity-tests]]
