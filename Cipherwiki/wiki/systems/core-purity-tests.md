---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, testing, purity, determinism]
---

# CorePurityTests — กลไกบังคับใช้กฎข้อ 2 และ 10

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

`CorePurityTests` **สะท้อน (reflect) ตัว assembly ที่ compile แล้ว** และ fail ถ้ามี type ต้องห้ามโผล่บน **public surface** ของ Core หรือใน **reference table**

## ตรวจสองระดับ: type และชื่อ

| ระดับ | จับอะไร | ตัวอย่างที่ผ่าน/ไม่ผ่าน |
|---|---|---|
| **Type-level** | `UnityEngine.*`, `System.Random`, `DateTime`, `Guid`, `File`, `Directory`, `Environment` | `Guid` เป็น type ต้องห้าม → **fail** |
| **Name-level** | member ที่มีรูปพิกัด | field ชื่อ `GridX` เป็น `int` ธรรมดา → **ผ่าน check ระดับ type** |

> "A field called `GridX` passes every type-level check — **the type is an innocent `int`** — so the rule has to be enforced on **names as well as types**"

นี่คือข้อจำกัดที่สำคัญของการตรวจแบบ reflection: **ชื่อที่เป็นรูปพิกัดแต่มี type ที่ดูไม่ผิดจะหลุดได้** การตรวจชื่อจึงเป็นชั้นที่สองที่จำเป็น ไม่ใช่การเพิ่มที่เกินจำเป็น

## กลไก determinism สี่ชั้น

ARCHITECTURE.md จัดเรียงตาม "order of how much they matter":

1. **Named RNG streams** — `RngStreams` derive ห้า generator จาก world seed ดู [[systems/irng-streams]]
2. **Integer arithmetic ใน rule paths** — `XorShift128Rng` ใช้เฉพาะ integer op ที่ exact, `BaseLayout.ApplyDepthCost` ปัดด้วย integer division
3. **No ambient state** — ตัวที่ test นี้ตรวจ
4. **No coordinates** — ตรวจด้วย **member name** ดู [[systems/grid-to-slot-migration]]

> "Stage 5 adds the **full grep audit and a Roslyn analyzer**" — ยังไม่มี ดู [[stage-order]]

## `XorShift128Rng`

อัลกอริทึม RNG ที่ Core ใช้ — ใช้ **เฉพาะ integer operation** เพื่อให้ผลลัพธ์เหมือนกันทุก platform นี่คือการ implement [[determinism]] ข้อ 2 ในระดับอัลกอริทึม

**เหตุผลที่เลือก xorshift ไม่ใช่ LCG หรือ Mersenne:** ทั้งหมดเป็น integer op ได้ แต่ xorshift มี state ขนาดเล็กและตรวจสอบย้อนหลังได้ ซึ่งเหมาะกับการมี named streams แยก

> **เป็นการอนุมาน:** เหตุผลข้างบนเป็นการตีความจากคุณสมบัติ ไม่ได้ระบุในเอกสาร — ถ้ามีเหตุผลอื่นควรบันทึกไว้

## ช่องว่างที่ยังอยู่

กลไกบังคับใช้ครอบคลุม**ชนิด type และชื่อสมาชิก** แต่ไม่ครอบคลุม:

- การ **เรียก** `System.IO` ผ่าน indirection ที่ type check มองไม่เห็น
- **behavioral** determinism เช่น unordered `Dictionary` enumeration ซึ่ง ARCHITECTURE.md พูดถึงใน [[systems/tech-stack]] แต่ **ไม่มีกฎใน `raw/knowledge.md` และไม่มี test**

## เชื่อมโยง

- [[core-purity]] — กฎข้อ 2
- [[slot-layer-abstraction]] — กฎข้อ 10 ที่ enforce ผ่าน member name
- [[determinism]] — กฎข้อ 6 ที่กลไกทั้งสี่ดูรับ
- [[systems/irng-streams]] — กลไกชั้นที่ 1
- [[systems/grid-to-slot-migration]] — กลไกชั้นที่ 4 และตัวที่บังคับไม่ให้ `GridX` กลับมา
- [[systems/simulation-rules]] — `ApplyDepthCost` ที่ปัดด้วย integer division
- [[systems/game-session]] — กลไก double-validate ที่จับ non-determinism ที่ test อาจไม่ครอบคลุม
- [[systems/determinism-tests]] — การพิสูจน์ที่แยกจากการป้องกัน
- [[systems/table-validator]] — test บังคับใช้กฎข้อ 7
- [[stage-order]] — grep audit และ Roslyn analyzer ผังไว้ Stage 5
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] Name-level check ใช้รายการชื่อที่เขียนไว้ หรือ heuristic
- [ ] `CorePurityTests` เช็ค reference table ของ assembly ลึกแค่ไหน — dependency ตัวที่ 2 ระดับจะถูกจับหรือไม่
- [ ] มี test ที่จับ unordered `Dictionary` enumeration หรือยัง (ดูช่องว่างข้างบน)
