---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, commands, sessions]
---

# GameSession — ประตูทางเข้าเดียวของ mutation

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

`GameSession` คือ **assembly จริงของ `CommandLog`** และเป็นทางเข้าสู่ `WorldState`

```
GameSession
  ├── Execute(cmd)
  ├── AdvanceTick()
  └── CommandLog
```

## Command flow

Presentation ไม่มีวัน mutate โลก — มัน**บรรยายความตั้งใจ** แล้ว Core ตัดสินว่าความตั้งใจนั้นถูกกฎหรือไม่และมีผลอย่างไร

```
   Player clicks "Build"
            │
            ▼
   new BuildRoomCommand(TypeId, Layer, SlotIndices, AdjacentRoomIds, NameKey, TotalCost)
            │
            ▼
   GameSession.Execute(command)
            │
            ▼
   command.Validate(world) ──► CommandResult
            │                        │
      side-effect free       ┌───────┴────────┐
                            │                │
                          Ok              Rejected(reason, args)
                            │                │
                            ▼                ▼
                    command.Apply(world,   state untouched;
                     world.RngStreams[...]) publish CommandRejected
                            │
                            ▼
                    append to CommandLog
                            │
                            ▼
                    queue GameEvents ──► flush ──► subscribers
```

## สามคุณสมบัติที่ได้จากการแยก Validate / Apply

1. **ทุก mutation ถูก log** — เพราะ `Execute` เป็นทางเดียวที่เปลี่ยน state `CommandLog` + `Seed` อธิบายทั้ง run ได้ นั่นคือสิ่งที่ `ReplayVerifier` ของ Stage 5 re-execute เพื่อพิสูจน์ [[determinism]]

2. **Rejection ปลอดภัยโดยโครงสร้าง** — command ที่ถูกปฏิเสธไม่เปลี่ยนอะไรเลย ดังนั้น UI จะเรียก `Validate` เพื่อทำ **ghost preview และ button gating** ได้อย่างอิสระโดยไม่มี side effect

3. **Core ไม่เขียน prose** — `CommandResult` ถือ `CommandReason` enum และ `CommandArgs` struct Presentation มองหา `command.reject.<reason>` ในตาราง localize แล้วแทนที่ args ไทยเป็นค่าเริ่มต้น ดู [[reason-code]]

## Double-validate — จุดที่ออกแบบมาเพื่อ determinism

> `GameSession.Execute` re-runs `Validate` a second time before applying and **throws if the result disagrees**. A command that passes once and rejects on the second call is non-deterministic, which would corrupt the log — that is a bug worth **crashing on** rather than papering over.

นี่เป็นการแปลง [[determinism]] จากหลักการเป็น **กลไกตรวจจับที่รันทุกครั้ง**: ถ้า `Validate` ขึ้นกับ state ที่เปลี่ยนไประหว่างเรียกสองครั้ง เกมจะพังทันทีแทนที่จะบันทึก log ที่ replay ไม่ได้

`Validate` จึงต้องเป็น **side-effect free** เป็นเงื่อนไขของกลไกนี้ ไม่ใช่แค่แนวปฏิบัติ

## Assembly ที่โหลด Core.dll เดียวกัน

```
data/*.csv ──pwsh tools/gen.ps1──> src/ProjectSpy.Tables/Gen (C# beans)
                                    assets/data/tables (*.bytes)

PROJECTSPY.CORE      (netstandard2.1)   ← ตรรกะทั้งหมด
PROJECTSPY.TABLES    (netstandard2.1)   ← compiled data จาก Luban
PROJECTSPY.SIM       (net8.0 console)   ← headless play/sim/verify, Stage 6+
Unity                                   ← Presentation, Stage 7+
xUnit                                   ← tests
```

> "Three assemblies load the same `ProjectSpy.Core.dll`: Unity, the console simulator and the test runner. That is the reason Core targets `netstandard2.1` and **carries no third-party dependencies**"

**"carries no third-party dependencies"** เป็นข้อที่ควรระวัง — แต่ Core **อ้าง `ProjectSpy.Tables` โดยตรง** ตาม [[systems/simulation-rules]] ซึ่งเป็น first-party จึงไม่ขัด แต่ถ้ามีการเพิ่ม NuGet package ใดๆ ใน Core จะละเมิดข้อความนี้ ดู [[core-purity]]

เหตุผลที่ต้องไม่มี dependency: *"a behaviour difference between the simulator and the game would make the stage-6 balance harness worthless"* — ถ้า simulator กับเกมต่างกัน harness จะไม่บอกความจริง

## เชื่อมโยง

- [[systems/command-log]] — ภาพรวมของ command flow
- [[systems/game-event-buffer]] — ทำไม event ต้องผ่าน buffer
- [[systems/world-state]] — สิ่งที่ `Execute` แก้
- [[systems/simulation-rules]] — Core อ้าง `ProjectSpy.Tables` โดยตรง
- [[systems/replay]] — สิ่งที่ `CommandLog` + `Seed` ใช้ replay
- [[layering]] — กฎข้อ 8 ที่ diagram นี้ทำให้เห็นชัด
- [[core-purity]] — เหตุผลของ netstandard2.1 + ไม่มี dependency
- [[reason-code]] — `CommandReason` + `CommandArgs`
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] `Validate` ของ command แต่ละชนิดตรวจอะไรบ้าง และมี command กี่ชนิด
- [ ] "Core carries no third-party dependencies" — `ProjectSpy.Tables` นับเป็น first-party อย่างเป็นทางการหรือไม่
- [ ] Ghost preview ใช้ `Validate` ซ้ำกันได้ถี่แค่ไหนโดยไม่กระทบ tick cost
