---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, replay, determinism]
---

# Replay — บันทึกลำดับ ไม่ reconstruct

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

`GameSession` บันทึก **log อย่างเดียวของ commands และ tick advances** เป็น `ReplayEntry` และ `Replay` จะ re-execute ตามลำดับนั้น

## ปัญหาของการมี log สองอัน

Stage 1 เก็บ **สอง log ขนาน** — รายการ command กับรายการ tick batch — แล้ว replay command ทั้งหมด**ก่อน** tick batch แรก

> "That works only while the player issues commands **exclusively before the first tick**. As soon as they do not — build a room, play a week, build another — **both builds replay before the week ever runs**, and the final state differs for reasons that have nothing to do with determinism."

> "A determinism test built on that arrangement would have **proved nothing**, because the divergence would have been in the harness."

นี่คือกรณีที่ควรอ่านช้า: **harness ที่ผิด ทำให้เกมที่ถูกต้องดูเหมือนไม่ deterministic** ตอนนี้ลำดับถูก**บันทึกตรงๆ** ไม่ใช่ reconstruct

## Tick advance ถูกบันทึกเป็น batch ก้อนเดียว

> "Tick advances are recorded as a **single batch entry** rather than N singles, because **the batching decides whether a periodic rule (weekly settlement, pool refresh) fires**. It has to be replayed exactly rather than inferred."

เชื่อมกับ guard `WorldState.LastSettledWeek` ใน [[systems/tick-pipeline]] — ถ้า batch ถูกแตกเป็น N รายการ การ settle จะเกิดผิดจำนวนครั้ง

## การพิสูจน์ determinism

| ตัว | หน้าที่ |
|---|---|
| `WorldStateSerializer.ToCanonicalBytes` | **ผู้มีอำนาจ** ของการเทียบ byte |
| `WorldState.ComputeStateHash()` | การตรวจที่ถูก — hash ไม่ครอบคลุมทุก field จึงอาจชนกันได้ |
| `VerifyReplay` (Stage 5) | เทียบ hash |

> "two different worlds can hash alike if the hash does not cover every field, so **the serializer is the authority** and the hash is the cheap check"

`ComputeStateHash()` รวม **RNG state** เข้าไปด้วย เพราะ "two worlds that look identical but will roll differently are **not the same world**, and the hash must say so" ดู [[systems/world-state]] และ [[systems/irng-streams]]

## Bug report ที่ reproduce ได้

ผลประโยชน์ของ replay ตาม [[systems/tech-stack]]: **ผู้เล่นส่ง seed + command log มา → reproduce ได้เป๊ะ** — ทำได้เพราะ `CommandLog` + `Seed` อธิบายทั้ง run ตาม [[systems/game-session]]

## เชื่อมโยง

- [[systems/command-log]] — ข้อมูลที่ replay ใช้
- [[systems/game-session]] — `Execute` ที่บันทึก log
- [[systems/determinism-tests]] — test ที่พิสูจน์ทั้งสองเส้นทาง
- [[systems/world-state]] — `ComputeStateHash`
- [[systems/game-event-buffer]] — event ที่ flush ไม่กระทบ replay (event เป็น read-only output)
- [[determinism]] — กฎข้อ 6 ที่กลไกนี้พิสูจน์
- [[determinism-tests]] — counterweights ที่กัน test ผ่านเปล่าๆ
- [[systems/tech-stack]] — ประโยชน์ "Replay/Bug report"
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] `ReplayEntry` เก็บ command แบบเต็ม (พร้อม args) หรือ id + args
- [ ] `VerifyReplay` เขียนแล้วหรือยัง — เอกสารระบุว่าเป็น **Stage 5** ซึ่งยังไม่เริ่ม
- [ ] replay รองรับการ replay จาก tick กลาง (partial) หรือต้องเริ่มจาก seed เสมอ
- [ ] save file กับ replay log เป็นคนละสิ่งกันจริงหรือ — ดูคำถามเรื่อง MessagePack ใน [[systems/tech-stack]]
