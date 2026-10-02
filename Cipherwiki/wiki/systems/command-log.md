---
type: system
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, core, replay, commands]
---

# CommandLog — ทุก mutation เดินทางผ่านทางนี้

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 6 และ 8)
> **อัปเดต:** 2026-10-02

ทุกการเปลี่ยนแปลงของโลกต้องผ่าน **`ICommand.Execute`** เพื่อให้ทุก state change ถูก log และจึง **replay ได้** นี่คือข้อกำหนดเชิงโครงสร้าง ไม่ใช่แค่แนวปฏิบัติ — ถ้ามี mutation ทางอื่น Core จะถือ state ที่ command log ไม่มี log อธิบาย ซึ่งทำให้ [[determinism]] พังทันที

## เส้นทางของ action ทุกอย่าง

```
ผู้เล่น/Presentation → สร้าง ICommand → GameSession.Execute → Core แก้ WorldState
                                                              → บันทึกลง CommandLog
                                                              → ออก GameEvent stream
```

`README.md` เสริมว่า Presentation อ่าน result และ stream ของ `GameEvent` ด้วย — **Core เป็นสิ่งเดียวที่ mutate `WorldState`** ตัวอื่นอ่านอย่างเดียว

## Validate แยกจาก Apply

`ARCHITECTURE.md` ให้รายละเอียดว่า flow จริงคือ:

```
command.Validate(world) ──► Ok              ──► command.Apply(world, world.RngStreams[...])
                       └──► Rejected(reason, args) ──► state ไม่เปลี่ยน, publish CommandRejected
                                                                        │
                                                            append to CommandLog
                                                                        │
                                                            queue GameEvents ──► flush
```

`Validate` เป็น **side-effect free** — จึงเรียกซ้ำได้ (UI ใช้ทำ ghost preview) และ `Execute` เรียกมัน **สองครั้ง** แล้ว throw ถ้าผลไม่ตรงกัน ดู [[systems/game-session]]

## ⚠️ tick cost ยังไม่มี

> "`ICommand` still has no tick cost. The rule requires every rule-affecting action to carry one, and today commands apply instantly; **the first timed actions arrive with stage 4's mission interiors**"

นี่คือ **ช่องว่างระหว่างกฎกับ implementation**: กฎข้อ 6/10 บอกว่าทุก action ที่มีผลต่อกฎต้องมี tick cost แต่ตอนนี้ command ทุกตัว apply ทันที ดู [[systems/grid-to-slot-migration]] และ [[design/mission-and-fog-of-war]]

## เหตุผลที่ rule path ต้องมี tick cost

**ทุก action ที่มีผลต่อกฎคือ `ICommand` ที่มี tick cost** ตัวอย่างที่ชัดที่สุดคือการเดินในห้อง: ถ้า Core เก็บ `position` ของ agent ผู้เล่นจะเดินได้โดยไม่ issue command การเดินนั้นจะไม่อยู่ใน log และ replay จะไม่ได้ผลเดิม → นี่คือเหตุผลที่ [[slot-layer-abstraction]] เรียกร้อง slot/layer แทนพิกัด

คำสั่งภารกิจใน [[design/mission-and-fog-of-war]] (`เดินต่อ` · `รอเงียบๆ (ลด Alarm, เปลือง tick)`) คือคำสั่งกลุ่มแรกที่จะต้องมี tick cost จริง — `รอเงียบๆ` ทำให้เห็นว่า "เปลือง tick" เป็นกลไกที่ผู้เล่นจ่ายด้วยเวลา

## สองอย่างที่ต้องอยู่ด้วยกัน

CommandLog ไม่มีความหมายถ้าไม่มี determinism และไม่มีความหมายถ้าไม่มีกัน mutation ทางอ้อม — ทั้งสองอย่างอยู่ในกฎเดียวกัน ([[determinism]] ข้อ 6 + [[layering]] ข้อ 8) และล้มพลักกัน

## เชื่อมโยง

- [[systems/game-session]] — flow จริงของ `Execute` และ double-validate
- [[systems/game-event-buffer]] — event ที่ถูก queue แล้ว flush
- [[systems/replay]] — `CommandLog` + `Seed` คือทั้ง replay input
- [[systems/grid-to-slot-migration]] — `BuildRoomCommand` ที่เปลี่ยนรูป และช่องว่างเรื่อง tick cost
- [[systems/grid-commands]] — สะพานสำหรับ test ที่คิดเป็น grid
- [[determinism]] — seed + ordered command log → byte-identical `WorldState`
- [[layering]] — Presentation ไม่มีสิทธิ์แก้ `WorldState` ตรง ๆ
- [[tick-pipeline]] — ลำดับ phase ที่ command ถูก execute ผ่าน
- [[slot-layer-abstraction]] — เหตุผลว่าทำไม "เดิน" ต้องเป็น command
- [[design/mission-and-fog-of-war]] — คำสั่งภารกิจที่จะมี tick cost
- [[sources/knowledge]] — กฎข้อ 6, 8

## หมายเหตุ

- ยังไม่ได้ ingest `README.md` และ `docs/ARCHITECTURE.md` ซึ่งน่าจะมีรายละเอียดของ `GameSession`, `CommandLog` และ `GameEvent` มากกว่านี้

## คำถามที่ยังไม่มีคำตอบ

- [ ] `docs/SAVE-FORMAT.md` อยู่ที่ไหน — `README.md` อ้างถึงแต่ไม่มีใน `raw/` (และยังไม่ได้ ingest เพื่อยืนยัน)
- [ ] `CommandLog` เก็บอะไรพอดี — ตัว command เต็ม, id + args, หรือ diff
- [ ] command ถูก execute เป็น batch ต่อ tick หรือทีละตัว และ tick cost ถูกนับตรงไหน
- `docs/SAVE-FORMAT.md` ถูกอ้างถึงใน `README.md` แต่ไม่มีไฟล์นี้ใน `raw/` — เป็นช่องว่างของแหล่งอ้างอิง (ดูคำถามด้านล่าง)
