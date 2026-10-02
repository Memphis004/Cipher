---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, tick, determinism]
---

# TickPipeline — ลำดับหก phase ที่เป็น save contract

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 6) + [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

ลำดับการประมวลผลต่อ tick ต่อไปนี้เป็น **ส่วนหนึ่งของ save contract**:

```
RoomConstruction → Training → Recovery → MissionProgress → EventChecks → StatusDecay
```

ประกาศ **ครั้งเดียว** ใน `TickPipeline.CanonicalOrder` และ **assert โดย `TickOrderTests`**

## Phase ถูกเก็บใน dictionary แล้วรันตาม enum order

> "Phases are stored in a dictionary and executed in enum order, so **a phase registered later cannot accidentally run in the wrong slot**"

นี่เป็นกลไกป้องกันที่ดี — ลำดับจึงไม่ได้ขึ้นกับ insertion order แต่การมี dictionary ทำให้ "ลำดับ" มีได้จริงแค่ตัวเดียวในโค้ด

## ทำไมลำดับนี้ถึงห้ามสลับ

ARCHITECTURE.md ให้เหตุผลของ **แต่ละข้อจำกัดคู่** แยกกัน — และนี่คือส่วนที่ตอบคำถามที่เคยค้างในหน้าเดิม:

| Constraint | อะไรพังถ้าสลับ |
|---|---|
| `RoomConstruction` before `Training` | ห้องที่เพิ่งก่อสร้างเสร็จใน tick นี้จะใช้ไม่ได้จน tick หน้า — **ห้องใหม่ทุกห้องแอบเสีย tick ให้ผู้เล่นเงียบๆ** |
| `Training` before `Recovery` | การฝึกจะไม่มีต้นทุนจริง: stamina ที่ training ใช้ถูกเติมกลับใน tick เดียวกัน |
| `Recovery` before `MissionProgress` | สปายออกภารกิจด้วย stamina ที่เหลือจาก**เมื่อวาน** ไม่ใช่ของวันนี้ |
| `EventChecks` after `MissionProgress` | ภารกิจที่จบใน tick นี้ยังไม่ถูก resolve ตอนที่ event roll → **กระทบ roll ของ tick ผิด** |
| `StatusDecay` last | drift, morale และ burnout ถูกคำนวณจากโลกที่อัปเดตเพียงครึ่งทาง |

ข้อแรกและข้อสองน่าสังเกตที่สุด เพราะเป็นกรณีที่ **ผู้เล่นจะรู้สึกว่าเกมถูกลงโทษโดยไม่มีข้อความแจ้งเตือน** — ซึ่งเป็นหลักการเดียวกับที่ `knowledge.md` ใช้กับ empty method body: "ความเงียบถูกอ่านว่า 'ทำเสร็จแล้ว'"

## Phase implementations

**ทั้งหก phase implement แล้ว ณ Stage 3** ผ่าน `Phases.CreateDefault`

`MissionProgress` **บางโดยเจตนา**: มัน advance รายการภารกิจและ apply **mole leak roll** แต่**ยังไม่มีการ generate ภารกิจ** เพราะยังไม่มีใครสร้าง mission

`MoleSystem.AccumulatedLeakDifficulty` คือ **seam ที่ Stage 4 อ่าน** — ภารกิจใหม่เริ่ม difficulty จากค่านั้น ดู [[systems/mole-system]]

`EventChecks` แบกงานรายสัปดาห์ไว้ด้วย: settlement, mole's Heat pass, contract refresh และ counter-intel step budget — ทั้งหมดถูก guard ด้วย **`WorldState.LastSettledWeek`**

> เหตุผลของ guard: การ advance tick เป็นก้อนใหญ่ต้องยัง **settle ทุกสัปดาห์ exactly once** ไม่ใช่หนึ่งครั้งต่อทุก tick ที่บังเอิญตรงวันจ่ายเงิน

จุดนี้เชื่อมกับ [[systems/replay]]: tick advance ถูกบันทึกเป็น **batch entry ก้อนเดียว** เพราะ "the batching decides whether a periodic rule fires. It has to be replayed exactly rather than inferred."

## `TickOrderTests` assert อะไร

สามอย่าง:

1. assert ลำดับลำดับ phase
2. assert ว่าลำดับ**ไม่ขึ้นกับ registration order**
3. assert **แต่ละ pairwise relationship แยกกัน**

ข้อ 3 คือเหตุผลที่ตารางข้างบนมีค่า: "so a future reorder fails as a **named constraint** rather than as an unexplained simulation change" — test จะบอกว่าละเมิดข้อไหน ไม่ใช่แค่บอกว่าผลลัพธ์เปลี่ยน

## เชื่อมโยง

- [[systems/game-clock]] — `AdvanceTick()` และ tick เดียวที่ขับ pipeline
- [[systems/tick-order-tests]] — รายละเอียด test
- [[systems/mole-system]] — `AccumulatedLeakDifficulty` ที่ `MissionProgress` stage ไว้
- [[systems/command-log]] — event ที่ phase ผลิตถูกบัฟเฟอร์
- [[systems/game-event-buffer]] — phase ไม่ publish ตรง
- [[determinism]] — กฎข้อ 6 ที่บังคับให้ลำดับนี้ตายตัว
- [[design/agents]] — Training, Recovery, StatusDecay ขับ stat และ burnout
- [[design/time-and-calendar]] — settlement ทุก 7 วัน
- [[out-of-scope-bodies]] — ถ้า phase ใดยังไม่ทำ ต้อง throw พร้อม `// TODO(stage-N):`
- [[sources/knowledge]] — กฎข้อ 6
- [[sources/ARCHITECTURE]] — ที่มาของรายละเอียดกลไก

## คำถามที่ยังไม่มีคำตอบ

- [ ] แต่ละ phase กินเวลา tick เท่าไร — ดูเดิม แต่ ARCHITECTURE.md ก็ยังไม่ตอบ
- [ ] ถ้า command ถูก execute ผิด phase ระบบจัดการอย่างไร — ยังไม่มีคำตอบ
- [ ] `WorldState.LastSettledWeek` เป็น integer tick หรือเลขสัปดาห์
