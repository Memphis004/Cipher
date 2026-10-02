---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, state, serialization]
---

# WorldState — รากของ state ทั้งหมด

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

```
WorldState (serializable root)
  Seed · Clock · Resources
  BaseLayout · Agents · Recruits
  Missions · Contracts · Flags
  RngStreams
```

`WorldState` ถูกอ้างถึงใน [[systems/game-session]] เป็นสิ่งที่ `ICommand` validate และ apply ลง และเป็นสิ่งที่ serialize เพื่อเทียบ byte

## `ComputeStateHash()`

> "produces a **stable, integer-only fingerprint** including **RNG state** — two worlds that look identical but will roll differently are **not the same world**, and the hash must say so"

**รวม RNG state เข้าไปด้วย** เพราะ [[determinism]] ไม่ได้แปลว่า state เท่ากัน แต่แปลว่า **roll ต่อไปเหมือนกัน** — สองโลกที่ดูเหมือนกันแต่ RNG ต่างกันคือคนละโลก ดู [[systems/irng-streams]]

> **เป็นการอนุมาน:** "integer-only" น่าจะมีเหตุผลเดียวกับ [[systems/core-purity]] — hash ที่คำนวณจาก float จะไม่ stable ข้าม platform ดังนั้น hash จึงเป็น enforcement อีกชั้นของกฎ "no float in rule paths"

## `WorldStateSerializer` ยังไม่ใช่ save file

> "**Stage 5:** save format, migrations, `ReplayVerifier`. `WorldStateSerializer` is a **fingerprint-grade canonical dump, not a save file**; it exists to make the byte-level determinism guarantee **checkable**"

นี่ตอบคำถามที่ค้างใน [[systems/tech-stack]]: MessagePack ถูกเลือกเป็น save format ใน tech stack แต่ **ยังไม่ได้ implement** — สิ่งที่มีคือ serializer สำหรับเทียบ byte เท่านั้น

ดังนั้น **save file จริงยังไม่มี** และ `README.md` ระบุว่า table binaries จะถูก copy เข้า `Assets/StreamingAssets` ใน Stage 7 ดู [[sources/README]]

## `Flags` และ `LastSettledWeek`

`Flags` เป็นพื้นที่สำหรับสถานะบูตที่ไม่ fit ใน field อื่น

`WorldState.LastSettledWeek` เป็นตัวที่ guard งานรายสัปดาห์ใน `EventChecks` เพื่อให้การ advance tick เป็นก้อน settle แค่ครั้งเดียว ดู [[systems/tick-pipeline]] และ [[systems/replay]]

> **เป็นการอนุมาน:** `LastSettledWeek` ไม่ได้อยู่ในรายการ field ของ `WorldState` ใน diagram แม้จะถูกอ้างถึงในข้อความ — อาจเป็น field ที่ diagram ตัดไว้ หรืออยู่ใน `Flags`

## BaseLayout

`BaseLayout` ถูกอ้างถึงใน [[systems/grid-to-slot-migration]] — เป็นตัวที่ถูกถอดพิกัดออก และเป็นเจ้าของ `ApplyDepthCost` ที่ปัดด้วย integer division

## เชื่อมโยง

- [[systems/game-session]] — สิ่งที่ command mutate
- [[systems/replay]] — serializer กับ hash
- [[systems/determinism-tests]] — test ที่ serialize state เพื่อเทียบ
- [[systems/irng-streams]] — `RngStreams` เป็น field ของ state
- [[systems/grid-to-slot-migration]] — `BaseLayout`
- [[systems/tick-pipeline]] — `LastSettledWeek`
- [[determinism]] — เหตุผลที่ hash ต้องรวม RNG state
- [[core-purity]] — เหตุผลที่ hash เป็น integer-only
- [[data-integrity]] — save ที่อ้างถึง id ที่หายต้อง quarantine
- [[systems/tech-stack]] — MessagePack ยังไม่ได้ implement
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] `ComputeStateHash` ใช้ algorithm อะไร และกี่ field ครอบคลุม — เอกสารเตือนเองว่าอาจไม่ครบ
- [ ] `Flags` เก็บอะไรบ้าง และมี type หรือเป็น set ของ key
- [ ] `WorldStateSerializer` เป็น class หรือ struct และอยู่ใน Core หรือ assembly แยก
- [ ] Save format จะรวม RNG state ด้วยหรือไม่ — ถ้าไม่ การโหลด save จะต้องกู้ RNG state มาจากที่อื่น
- [ ] `Clock` ใน `WorldState` คือ `GameClock` หรือค่า tick เปล่า ดู [[systems/game-clock]]
