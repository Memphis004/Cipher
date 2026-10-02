---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, determinism, core]
---

# Determinism — ความสามารถในการทำซ้ำได้อย่าง byte-identical

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 6)
> **อัปเดต:** 2026-10-02

Determinism เป็น **hard requirement** ไม่ใช่ความตั้งใจที่ดี — seed เดิมกับ ordered command log เดิมต้องให้ผลเป็น `WorldState` ที่ **byte-identical** ทดสอบด้วย test และภายหลังด้วย replay verifier

## สี่ข้อบังคับ

### 1. Randomness ผ่าน `IRng` เท่านั้น

ทุก subsystem ได้ **named stream ของตัวเอง** จาก `RngStreams`:

| Stream | ใช้กับ |
|---|---|
| World | |
| Mission | |
| Event | |
| Recruit | |
| Trait | |

เหตุผล: การเพิ่ม roll ในระบบหนึ่ง **ต้องไม่ทำให้ลำดับของอีกระบบเลื่อน** นี่คือเหตุผลที่ stream แยกกัน — ถ้าใช้ stream เดียวร่วมกัน การเพิ่ม random call ที่ใดก็ตามจะไปกวาดทั้งเกม ดู [[irng-streams]]

### 2. Rule paths ใช้ integer หรือ fixed-point

ความต่างของลำดับ `float`/`double` ระหว่าง platform คือ **determinism bug ไม่ใช่รายละเอียดการปัดเศษ** — นี่เป็นถ้อยคำตรง ๆ จากกฎ

### 3. Tick processing order ตายตัว

ลำดับนี้เป็น **ส่วนหนึ่งของ save contract**:

```
RoomConstruction → Training → Recovery → MissionProgress → EventChecks → StatusDecay
```

ต้อง fixed, ต้อง documented, และต้อง **asserted by a test** ดู [[tick-pipeline]]

> **ยืนยันแล้วว่ามีกลไก:** ประกาศใน `TickPipeline.CanonicalOrder` และ `TickOrderTests` assert ทั้งลำดับ, ความเป็นอิสระจาก registration order และ **แต่ละ pairwise relationship แยกกัน** พร้อมเหตุผลว่าสลับแล้วอะไรพัง ดู [[systems/tick-pipeline]] และ [[systems/tick-order-tests]]

### 4. Mutation ผ่าน `ICommand.Execute` เท่านั้น

ทุกการเปลี่ยนแปลง state ต้องถูก log จึง replay ได้ ดู [[command-log]] และ [[layering]]

## กลไกบังคับใช้ที่มีอยู่จริง

จากการ ingest [[sources/ARCHITECTURE]] มีกลไกสี่ชั้น เรียงตามน้ำหนัก:

| # | กลไก | สถานะ |
|---|---|---|
| 1 | Named RNG streams ห้าตัว | มีแล้ว ดู [[systems/irng-streams]] |
| 2 | Integer arithmetic (`XorShift128Rng`, `ApplyDepthCost`) | มีแล้ว |
| 3 | No ambient state (`CorePurityTests`) | มีแล้ว ดู [[systems/core-purity-tests]] |
| 4 | No coordinates (ตรวจด้วย **member name**) | มีแล้ว ดู [[systems/grid-to-slot-migration]] |

เพิ่มเติม: `GameSession.Execute` **validate ซ้ำสองครั้งแล้ว throw ถ้าผลไม่ตรงกัน** เพราะ command ที่ผ่านครั้งแรกแต่ตกครั้งที่สองคือ non-deterministic ซึ่งจะทำให้ log เสีย ดู [[systems/game-session]]

## Counterweights ที่กัน test ผ่านเปล่าๆ

[[systems/determinism-tests]] มี test สามตัวที่มีอยู่เพื่อกันกรณีเกมไม่ deterministic แบบไม่มีอะไรให้ตรวจ: `DifferentSeedsActuallyDiverge`, `ADifferentCommandLogProducesADifferentWorld`, `TheYearRunActuallyDoesSomething`

**determinism เป็นคุณสมบัติที่ "เสีย" ได้ง่ายที่สุด** — เกมที่ไม่ทำอะไรเลยก็ deterministic สมบูรณ์

## ประเด็นที่มักมองข้าม

**State ที่ไม่มี command อธิบาย = state ที่ replay ไม่ได้** นี่คือเหตุผลอันดับ 1 ของ [[slot-layer-abstraction]]: ถ้า Core เก็บ `position` ของ agent ผู้เล่นจะเดินโดยไม่ต้อง issue command และการเดินนั้นจะไม่อยู่ใน command log ซึ่งทำลายข้อนี้ทันที

## เชื่อมโยง

- [[core-purity]] — `System.Random`, `DateTime`, `Guid.NewGuid`, `File.`, `Environment.` ถูกห้ามเพราะเป็นแหล่งไม่ deterministic
- [[command-log]] — ทางเดียวที่ mutation จะถูกบันทึก
- [[tick-pipeline]] — ลำดับหก phase ที่เป็น save contract
- [[irng-streams]] — named streams เพื่อไม่ให้ sequence ของระบบหนึ่งไปกวาดอีกระบบ
- [[slot-layer-abstraction]] — เหตุผลเชิงระบบข้อ 1: determinism
- [[sources/knowledge]] — กฎข้อ 6

## Replay — กลไกที่พิสูจน์กฎนี้

`CommandLog` + `Seed` อธิบายทั้ง run ทำให้ผู้เล่นส่งมาแล้ว reproduce ได้ ดู [[systems/replay]]

> **บทเรียนจากการเขียน harness:** Stage 1 เคยเก็บ log สองอัน (command list กับ tick-batch list) แล้ว replay command ทั้งหมดก่อน tick แรก วิธีนี้ใช้ได้เฉพาะตอนผู้เล่นสั่ง command เฉพาะก่อน tick แรกเท่านั้น — **determinism test ที่สร้างบนโครงสร้างแบบนั้นพิสูจน์อะไรไม่ได้เลย** เพราะ divergence อยู่ใน harness ไม่ใช่ในเกม ตอนนี้ลำดับถูกบันทึกตรงๆ ดู [[systems/replay]]

## หมายเหตุ

Stage 3 ส่งมอบ headline guarantee สองข้อแล้ว: 365-day unattended run อยู่ในกฎทางการเงินและ reproducible และสอง session ที่ใช้ seed และ command log เดียวกันให้ byte-identical state — ดู [[sources/README]] และ [[systems/determinism-tests]]

> **ช่องว่างที่ยังอยู่:** `ICommand` ยัง **ไม่มี tick cost** ตามที่ `ARCHITECTURE.md` ระบุตรง ๆ ว่าจะมาพร้อม mission interiors ใน Stage 4 ดู [[systems/grid-to-slot-migration]]
