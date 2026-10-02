---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, core, mole, heat, counter-intel]
---

# MoleSystem — จุดเชื่อมระหว่าง mole กับ mission difficulty

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md) + [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.5, 1.8)
> **อัปเดต:** 2026-10-02

`MoleSystem.AccumulatedLeakDifficulty` คือ **seam ที่ Stage 4 อ่าน**

> "A new mission's difficulty starts from that total, so **a mole who has been leaking for three weeks makes the agency measurably worse at everything** without the player ever being told which agent is responsible"

## กลไกที่ implement แล้ว ณ Stage 3

- `MissionProgress` **apply mole leak roll** ในทุก tick
- `EventChecks` ทำ **mole's Heat pass** และ **counter-intel step budget** เป็นงานรายสัปดาห์
- ค่าสะสมถูกเก็บใน `WorldState` (ดู [[systems/world-state]])

> **ข้อควรระวัง:** mole leak roll เป็น random → ต้องใช้ stream ที่ถูกต้อง และห้ามใช้ global generator ดู [[systems/irng-streams]]

## ทำไม seam นี้ถึงสำคัญ

มันทำให้ mole มี**ผลกระทบที่วัดได้แต่ไม่ระบุตัวตน** — องค์กรแย่ลงอย่างค่อยเป็นค่อยไปโดยที่ผู้เล่นไม่รู้ว่าเพราะใคร ซึ่งคือหัวใจของ [[design/core-fantasy]]: Handler ต้องอยู่กับผลของการตัดสินใจที่ไม่มีข้อมูลพอ

**seam เป็นคำที่ตั้งใจใช้:** ณ Stage 3 ยังไม่มีการ generate mission เลย ดังนั้น `AccumulatedLeakDifficulty` เป็นค่าที่ **ถูกคำนวณแล้วแต่ยังไม่มีใครอ่าน** — เป็นการเตรียม contract ไว้ให้ Stage 4

## แขนงการออกแบบ

```
Mole (trait ที่ซ่อน)
   │ ส่งข้อมูลให้ศัตรู
   ▼
Heat เพิ่มเงียบๆ
   │
   ├──► AccumulatedLeakDifficulty ──► mission difficulty ของภารกิจใหม่ (Stage 4)
   │
   └──► ศัตรูบุกฐานเมื่อ Heat สูง ──► Base Defense

Counter-Intel (ห้อง + step budget)
   │ สืบหา ──► ถูก ──► ติดสินบน ──► Heat ลด
   │ ผิด ──► Loyalty ทั้งองค์กรตก
   ▼
Deserter (trait ที่ซ่อนที่อีกตัว) ──► Loyalty ต่ำแล้วหนีกลางภารกิจ
```

## Counter-intel balance ถูก validate เป็นกฎ

`TableValidator.ValidateCounterIntelBalance` ตรวจสองข้อ:

1. เคสต้อง **สรุปได้** (best case ผ่าน threshold)
2. เคสต้อง **ไม่สรุปโดยเฉลี่ย** (expected case ไม่ผ่าน)

> "The second half is as important as the first: evidence accrues identically whoever you investigate, so **a threshold below the expected case would have the desk exposing people at random** and make the player's own correlation work worthless"

นี่คือ [[scope-discipline]] ในรูปของ test: ป้องกันไม่ให้ feature ทำงาน "แต่ไม่ทำอะไรจริง" ดู [[systems/table-validator]]

## เชื่อมโยง

- [[design/agents]] — Mole และ Deserter เป็น trait ที่ซ่อน
- [[design/heat-and-counter-intel]] — Heat เพิ่มจาก mole, ลดจาก Comms Room และติดสินบน
- [[systems/tick-pipeline]] — `MissionProgress` ทำ leak roll, `EventChecks` ทำ Heat pass
- [[systems/irng-streams]] — leak roll ต้องใช้ stream ที่ถูกต้อง
- [[systems/table-validator]] — กฎ balance ของ counter-intel
- [[systems/simulation-rules]] — ค่า balance ทั้งหมดมาจากตาราง
- [[design/mission-and-fog-of-war]] — Stage 4 จะอ่าน `AccumulatedLeakDifficulty`
- [[design/base-building]] — ห้อง Counter-Intel ที่ใช้ step budget
- [[systems/world-state]] — `LastSettledWeek` ที่ guard งานรายสัปดาห์
- `raw/doc/MOLE_DESIGN.md` — ยังไม่ได้ ingest: เหตุผลเชิงลึกว่าทำไม mole ต้องเป็น mystery ที่ยุติธรรม, Heat/mission-log reasoning chain และอะไรพังถ้า tune ผิด (`ARCHITECTURE.md` อ้างถึงเป็นเอกสารถัดไป)
- [[sources/ARCHITECTURE]] — ที่มาของรายละเอียดกลไก
