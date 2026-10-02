---
type: system
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, core, determinism, rng]
---

# IRng และ RngStreams — random ที่แยกเป็นชื่อ

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 6) + [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

Randomness ทั้งหมดเดินผ่าน **`IRng`** และแต่ละ subsystem ได้ **named stream ของตัวเอง** จาก **`RngStreams`**

## Stream ที่ระบุไว้

| Stream | ใช้กับ (ตามชื่อ) |
|---|---|
| `World` | |
| `Mission` | |
| `Event` | |
| `Recruit` | |
| `Trait` | |

## เหตุผลของการแยก stream

ประโยคตามตัวอักษรของกฎ: *"เพื่อให้การเพิ่ม roll ในระบบหนึ่งไม่ทำให้ลำดับของอีกระบบเลื่อน"* — ถ้าใช้ stream เดียวร่วมกัน การเพิ่ม random call หนึ่งครั้งที่ใดก็ตามจะเลื่อน sequence ของทุกอย่างที่อยู่ถัดไป ซึ่งแปลว่า **การแก้ bug หนึ่งจุดทำให้ replay เก่าทั้งหมดผิด** การแยก stream ทำให้ blast radius ของการเพิ่ม roll อยู่ในระบบเดียว

นี่เป็นอีกตัวอย่างของหลักเดียวกับ [[slot-layer-abstraction]]: **Core ห้ามถือ state หรือพฤติกรรมที่การแก้ที่อื่นไปกวาดโดยไม่ตั้งใจ**

## ยืนยันจำนวน stream จาก implementation

`raw/doc/ARCHITECTURE.md` ยืนยันว่า:

> "`RngStreams` derives **five** independent generators from the world seed — World, Mission, Event, Recruit, Trait. Without this, **one extra roll in trait generation would shift every mission map afterwards**, invalidating saved replays and making bugs nearly impossible to bisect. Each command receives the stream it needs; **none shares a global generator**."

เหตุผลที่ระบุตรงนี้ยืนยันว่ากฎข้อ 6 มีที่มา — การเพิ่ม roll ในการสร้าง trait หนึ่งครั้งจะเลื่อน mission map ทุกแผนที่ถัดมา ซึ่งทำให้ replay ที่บันทึกไว้ใช้ไม่ได้และทำให้ bisect bug แทบเป็นไปไม่ได้

> **⚠️ หมายเหตุการขัดแย้ง:** สำเนา `knowledge.md` รุ่นเก่าที่ฝังใน `raw/GDD.md` ระบุ stream เพียง **สาม** ตัว (`WorldRng, MissionRng, EventRng`) — `raw/knowledge.md` ที่บังคับใช้ระบุ **ห้า** และ implementation ยืนยันห้า ดู [[sources/GDD]]

## จุดที่ stream ถูกใช้

- `ICommand.Apply(world, world.RngStreams[...])` — command แต่ละตัวรับ stream ที่ต้องการ ดู [[systems/game-session]]
- **`MissionProgress`** — mole leak roll ดู [[systems/mole-system]]
- **mission resolve** — `d100` ใน skill check ดู [[design/mission-and-fog-of-war]]
- **trait generation** — สุ่ม 1-3 traits ต่อสปาย ดู [[design/agents]]
- **recruitment** — ดู [[design/progression]] (Act 2 มีคู่แข่งปรากฏ)

## RNG ถูกเก็บใน state เพราะเป็นส่วนหนึ่งของตัวตนโลก

`RngStreams` เป็น field ของ `WorldState` ดู [[systems/world-state]] และ `ComputeStateHash()` รวม RNG state ใน hash เพราะสองโลกที่ดูเหมือนกันแต่จะ roll ต่างกัน **ไม่ใช่โลกเดียวกัน**

## `XorShift128Rng`

อัลกอริทึมที่ใช้จริง — ใช้เฉพาะ integer operation ที่ exact เพื่อให้ผลเหมือนกันทุก platform ดู [[systems/core-purity-tests]]

## เชื่อมโยง

- [[determinism]] — กฎข้อ 6 ที่บังคับให้ random ผ่าน `IRng` เท่านั้น
- [[core-purity]] — `System.Random` ถูกห้ามใน Core
- [[command-log]] — roll ที่เกิดจาก command ยังต้องถูก log
- [[tick-pipeline]] — แต่ละ phase อาจมี roll ของตัวเอง
- [[sources/knowledge]] — กฎข้อ 6

## คำถามที่ยังไม่มีคำตอบ

- [ ] seed ต่อ stream derive อย่างไรจาก world seed — เอกสารบอกว่า "derives five independent generators from the world seed" แต่ไม่ระบุสูตร
- [ ] stream ใหม่จะเพิ่มได้ผ่านอะไร — enum, config, หรือ registration
- [ ] stream ที่ไม่ถูกใช้เลยตลอด run ถือว่าเป็น bug หรือเป็นเรื่องปกติ
- [ ] เพิ่ม stream ใหม่ในภายหลัง (เช่น stream สำหรับ Stage 4) จะกระทบ replay ของ run ที่บันทึกไว้แล้วหรือไม่ — ถ้า derive เป็น index ตาม enum การแทรก stream กลางจะเลื่อนทุก stream หลังจากนั้น
