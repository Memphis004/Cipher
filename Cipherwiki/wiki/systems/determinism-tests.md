---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, testing, determinism]
---

# DeterminismTests — การพิสูจน์กฎข้อ 6

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

`DeterminismTests` ถือ **headline guarantee สองข้อ** พร้อมด้วย counterweights ที่กันไม่ให้มันผ่านแบบเปล่าๆ

## ตาราง test ทั้งหมด

| Test | ป้องกันอะไร |
|---|---|
| `AYearOfUnattendedSimulationCompletesWithoutThrowing` | ไม่มี state combination ใดที่ throw |
| `AYearOfSimulationNeverProducesANegativeResource` | ตรวจ **ทุกวัน** ไม่ใช่แค่ตอนจบ |
| `AYearOfSimulationLeavesNoAgentInAStuckStatus` | referential integrity ของ room/status |
| `TwoSessionsWithTheSameSeedAndCommandLogAreByteIdentical` | **byte-level guarantee** |
| `ReplayingARunIntoAFreshSessionIsByteIdentical` | ตัว replay path เอง |
| `DifferentSeedsActuallyDiverge` | ว่า seed **ถูกใช้จริง** |
| `ADifferentCommandLogProducesADifferentWorld` | ว่า commands **มีผลจริง** |
| `TheYearRunActuallyDoesSomething` | ว่า run **ไม่ stable แบบไร้ผล** |

## เหตุผลที่ต้องมี counterweights

> "The last three exist because **a determinism test passes just as happily against a simulation that ignores its seed, ignores its commands, or does nothing at all**."

นี่คือประเด็นที่สำคัญกว่าที่ดูเผิน: **determinism เป็นคุณสมบัติที่ "เสีย" ได้ง่ายที่สุดของคุณสมบัติทั้งหมด** — เกมที่ไม่ทำอะไรเลยก็ deterministic สมบูรณ์ การพิสูจน์ว่าเกม deterministic จึงไม่พอ ต้องพิสูจน์ **สิ่งที่ถูกทำด้วย** ด้วย

เชื่อมกับ [[scope-discipline]]: `TheYearRunActuallyDoesSomething` กันกรณีที่ "ทำงานได้แต่ไม่ทำอะไร" ซึ่งเป็นรูปแบบเดียวกับที่ `knowledge.md` กฎข้อ 5 ห้าม (`// TODO(stage-N):` เพราะ "ความเงียบถูกอ่านว่า 'ทำเสร็จแล้ว'") ดู [[out-of-scope-bodies]]

## เส้นทางการเทียบ

```
TwoSessionsWithTheSameSeed...  ─┐
ReplayingARunIntoAFresh...     ─┴─> WorldStateSerializer.ToCanonicalBytes  (authoritative)
                                    └─> ComputeStateHash                     (cheap check)
```

ดู [[systems/replay]] ว่าทำไม serializer ถึงเป็นผู้มีอำนาจ

## 365-day run ตอบคำถามข้อ 1

`knowledge.md` ข้อ 1 กำหนดว่าเกมต้อง "fun headless in a terminal" — **test ชุดนี้คือการพิสูจน์ส่วนแรกของข้อนั้น**: เกมรันครบ 365 วันโดยไม่ล้ม และไม่มี resource ติดลบ และไม่มีสปายค้างในสถานะที่เป็นไปไม่ได้

ยังไม่ใช่ "สนุก" — นั่นคืองานของ Stage 6 ดู [[stage-order]] และ [[systems/tech-stack]]

## เชื่อมโยง

- [[determinism]] — กฎข้อ 6 ที่ test ชุดนี้พิสูจน์
- [[systems/replay]] — เส้นทาง byte comparison และ hash
- [[systems/tick-order-tests]] — test อีกชุดที่ assert save contract
- [[systems/table-validator]] — test ที่พิสูจน์กฎข้อ 7
- [[systems/core-purity-tests]] — test ที่พิสูจน์กฎข้อ 2 และ 10
- [[out-of-scope-bodies]] — หลักการเดียวกับ counterweight tests
- [[scope-discipline]] — การรายงานตรงไปตรงมาเรื่องสิ่งที่ test ยังไม่พิสูจน์
- [[systems/world-state]] — state ที่ถูก hash และ serialize
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] run 365 วันใช้ command log แบบไหน — บันทึกไว้ ใช้ seed ใด และรันกี่ครั้ง
- [ ] `StuckStatus` ที่ test ตรวจมีสถานะอะไรบ้าง
- [ ] มี test สำหรับ `DifferentSeedsActuallyDiverge` ในแง่ *กี่* seed ที่ต้อง diverge ก่อนถือว่าผ่าน
- [ ] `MissionProgress` ยังไม่ generate mission — run ปัจจุบันจึงไม่ได้ทดสอบ mission generation เลย (จะเป็นช่องว่างตอน Stage 4)
