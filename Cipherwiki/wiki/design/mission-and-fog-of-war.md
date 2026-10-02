---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, mission, fog-of-war, alarm]
---

# Mission & Fog of War ⭐

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.7)
> **อัปเดต:** 2026-10-02

> **นี่คือระบบที่ต้องออกแบบให้ดีที่สุด เพราะเป็นจุดขายของเกม** — Stage 4 ยังไม่เริ่ม

## โครงสร้างแผนที่ (Node-based Procedural)

```
         [Vent]──[Hall]──[Lab]
           │       │       │
[ENTRY]──[Lobby]──[Sec]──[OBJECTIVE]──[Server]
           │       │                     │
         [Stair]─[Garage]──────────[EXTRACTION]
```

- สร้างด้วย **layered graph**: Entry layer → Transit layers → Objective → Extraction
- การันตีว่ามีเส้นทางถึง Objective เสมอ และ**มีทางเลือกอย่างน้อย 2 เส้น**
- แต่ละ node มี: `RoomType`, `SecurityLevel`, `Contents` (loot/guard/trap/terminal), `NoiseLevel`

## Fog of War — ผูกกับ Infiltration

| สถานะ node | เงื่อนไข |
|---|---|
| **Hidden** (ไม่เห็นเลย) | ไกลเกินรัศมี |
| **Silhouette** (เห็นว่ามีห้อง แต่ไม่รู้มีอะไร) | ในรัศมี `Infiltration / 25` |
| **Scouted** (เห็นประเภทห้อง + ระดับ security) | ในรัศมี `Infiltration / 40` หรือใช้ gadget/แฮ็กกล้อง |
| **Revealed** (เห็นทุกอย่าง) | เคยเข้าไปแล้ว |

> **สูตรนี้คือแกนการเติบโตทั้งเกม:** Infiltration 20 = เห็นแค่ห้องติดกัน (เดินเดาสุ่ม เครียดมาก) → Infiltration 90 = เห็นล่วงหน้า 3 ห้อง วางแผนเส้นทางได้ (รู้สึกเป็นมืออาชีพ) **ความสนุกคือการได้เห็นมากขึ้นเรื่อยๆ**

นี่คือ **progression ที่ไม่ใช้ level-up** — ผู้เล่นไม่ได้เก่งขึ้นเพราะได้เลข แต่เพราะ**เห็นมากขึ้น** ซึ่งเป็นการเปลี่ยนคุณภาพข้อมูล ไม่ใช่ค่าผ่านด่าน ดู [[design/agents]]

## การ Resolve แต่ละ node

```
เข้า node → roll event จาก mission_event ตาม RoomType + SecurityLevel
          → skill check: d100 + AgentStat + GadgetBonus + TeamSupport
                         vs  DifficultyClass(node)
          → ผลลัพธ์ 4 ระดับ:
             Critical Success → ผ่านเงียบ + loot พิเศษ
             Success          → ผ่าน, Alarm +0~5
             Partial           → ผ่าน แต่ Alarm +10~20, PS/MS -
             Failure           → Alarm +25, บาดเจ็บ, อาจติดอยู่ในห้อง
```

`mission_event` เป็น **ตาราง CSV** ตาม [[magic-numbers]] — `d100` ต้องมาจาก `Mission` stream ตาม [[systems/irng-streams]]

## Alarm Meter (0-100) — ตัวสร้างความกดดัน

| ระดับ | ผล |
|---|---|
| 0-25 Calm | ปกติ |
| 26-50 Suspicious | ยามเดินตรวจเพิ่ม, DC +10 |
| 51-75 Alert | ปิดประตูบางบาน, เส้นทางหาย, ศัตรูไล่ล่า |
| 76-99 Lockdown | Extraction ปิดบางจุด, ต้องสู้ออก |
| 100 Burned | **บังคับจบภารกิจ** — ใครยังไม่ถึง Extraction เสี่ยงถูกจับ/ตาย |

**Burned คือ fail state ที่แข็ง** — มันไม่ได้แค่จบภารกิจ แต่ฆ่าคนที่ยังไม่ออก ซึ่งเป็น [[design/core-fantasy]] ในรูปของกลไก: Handler ที่เกินอดีตจะเสียทั้งทีม

## คำสั่งที่ผู้เล่นแทรกได้ระหว่างภารกิจ

`เดินต่อ` · `รอเงียบๆ (ลด Alarm, เปลือง tick)` · `ใช้ gadget` · `แยกทีม` · `บังคับเปิดประตู` · `ถอนตัวทันที`

**ทุกคำสั่งในรายการนี้คือ `ICommand` ที่มี tick cost** ตามกฎข้อ 6 และ 10 — `raw/doc/ARCHITECTURE.md` ระบุตรงว่า "`ICommand` still has no tick cost... the first timed actions arrive with stage 4's mission interiors" ดู [[systems/command-log]] และ [[systems/grid-to-slot-migration]]

## เชื่อมโยง

- [[design/agents]] — Infiltration กำหนดรัศมี, Nerve ลด Alarm, PS/MS ลดจาก Failure
- [[slot-layer-abstraction]] — `Contents` คือ `RoomContents` (interactables + `SlotIndex`), ไม่ใช่พิกัด
- [[systems/command-log]] — คำสั่งทุกอย่างต้องเป็น command
- [[design/heat-and-counter-intel]] — ภารกิจที่เสียงดันเพิ่ม Heat
- [[systems/tick-pipeline]] — `MissionProgress` เป็น seam สำหรับ Stage 4
- [[design/base-building]] — Ops Center กำหนดจำนวนภารกิจพร้อมกัน
- [[systems/mole-system]] — `AccumulatedLeakDifficulty` คือจุดต่อระหว่าง mole กับ mission difficulty
- [[magic-numbers]] — 25, 40, 0-25, +25, +10~20 ทั้งหมดต้องอยู่ใน CSV
- [[sources/GDD]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] "รัศมี" ที่ GDD พูดถึงวัดเป็นอะไร — จำนวน node, จำนวนชั้นของ graph, หรือ Manhattan distance บน slot
- [ ] Adjacency ของ mission map เป็น explicit data หรือ derive จาก graph structure
- [ ] DC +10 ที่ Suspicious บวกกับ `DifficultyClass(node)` อย่างไร — และ `TeamSupport` คำนวณจากอะไร
- [ ] Failure ที่ทำให้ "ติดอยู่ในห้อง" แก้ด้วยคำสั่งไหนในรายการข้างบน
