---
type: index
updated: 2026-10-02
---

# index.md — ดัชนีของ Wiki

ไฟล์นี้เป็น **แคตตาล็อกเนื้อหา** ของ wiki ทั้งหมด จัดกลุ่มตามหมวดหมู่ เพื่อให้ทั้งคนและ LLM ค้นหาเจอเร็ว

> กฎเดียว: **ทุกครั้งที่สร้าง แก้ หรือลบหน้าใน `wiki/` ต้องอัปเดตไฟล์นี้ทันที**
> เมื่อตอบคำถาม ให้เริ่มจากอ่านไฟล์นี้ก่อน แล้วค่อย drill down ไปหน้าที่เกี่ยวข้อง

## สถานะ

- หน้าทั้งหมด: 42
- แหล่งอ้างอิงใน `raw/`: ingest แล้ว **4 จาก 7**
- ยังไม่ได้ ingest: `raw/prompt.md`, `raw/doc/MOLE_DESIGN.md`, `raw/doc/FLOORPLAN_UI.md`
- อัปเดตล่าสุด: 2026-10-02

## หน้า

### sources — สรุปแหล่งอ้างอิง

| หน้า | สรุปหนึ่งบรรทัด | แหล่งที่มา |
|---|---|---|
| [[sources/knowledge]] | กฎ binding ทั้ง 10 ข้อ — authoritative ที่สุดใน vault | `raw/knowledge.md` |
| [[sources/GDD]] | design document 12 หัวข้อ + stage prompts 1–10 — **⚠️ ฝัง knowledge.md รุ่นเก่าที่ขัดแย้ง** | `raw/GDD.md` |
| [[sources/ARCHITECTURE]] | กลไกจริง: command/event flow, tick pipeline, determinism, grid→slot, balance | `raw/doc/ARCHITECTURE.md` |
| [[sources/README]] | โครงสร้าง repo, stage status, build commands, สิ่งที่ Stage 3 ส่งมอบ | `raw/README.md` |

### systems — ส่วนประกอบของเกม (entity pages)

| หน้า | สรุปหนึ่งบรรทัด | แหล่งที่มา |
|---|---|---|
| [[systems/game-session]] | ประตูเดียวของ mutation — Validate/Apply แยกกัน + double-validate | ARCHITECTURE |
| [[systems/game-event-buffer]] | pending buffer + `FlushEvents` กัน re-entrancy | ARCHITECTURE |
| [[systems/game-clock]] | tick เดียวชนิด `long`, 24/วัน, 168/สัปดาห์ | ARCHITECTURE |
| [[systems/command-log]] | ทุก mutation ถูก log; **tick cost ยังไม่มี** | knowledge, ARCHITECTURE |
| [[systems/tick-pipeline]] | หก phase + เหตุผลว่าสลับแล้วอะไรพัง ทีละคู่ | knowledge, ARCHITECTURE |
| [[systems/tick-order-tests]] | assert ลำดับ, registration order และ pairwise | ARCHITECTURE |
| [[systems/irng-streams]] | `RngStreams` derive ห้า generator + `XorShift128Rng` | knowledge, ARCHITECTURE |
| [[systems/replay]] | `ReplayEntry` บันทึกลำดับ + บทเรียนจาก harness ผิด | ARCHITECTURE |
| [[systems/determinism-tests]] | test 8 ตัว + 3 counterweights กัน test ผ่านเปล่า | ARCHITECTURE |
| [[systems/world-state]] | รากของ state, `ComputeStateHash`, serializer ≠ save file | ARCHITECTURE |
| [[systems/core-purity-tests]] | ตรวจทั้ง **type-level** และ **name-level** | ARCHITECTURE |
| [[systems/grid-to-slot-migration]] | ตาราง Was/Is ของการถอดพิกัดออกจาก Core | ARCHITECTURE |
| [[systems/grid-commands]] | shim แปลง grid → slot สำหรับ test และ UI | ARCHITECTURE |
| [[systems/simulation-rules]] | `SimulationRules`, `AreTablesLoaded`, Core อ้าง Tables ตรง | ARCHITECTURE |
| [[systems/table-validator]] | กฎ validate ตารางที่จับ "ระบบทำงานเงียบ" | ARCHITECTURE |
| [[systems/mole-system]] | `AccumulatedLeakDifficulty` — seam สำหรับ Stage 4 | ARCHITECTURE, GDD |
| [[systems/tech-stack]] | Unity 2022.3 LTS, VContainer, R3, MessagePack; ตัด blockchain | GDD |
| [[project-layout]] | โครงสร้าง repo, target framework, build/test commands | README |

### concepts — กฎและหลักการ

| หน้า | สรุปหนึ่งบรรทัด | แหล่งที่มา |
|---|---|---|
| [[core-purity]] | Core ห้าม `UnityEngine` และไม่มี third-party dependency | knowledge, ARCHITECTURE |
| [[determinism]] | seed + command log → byte-identical state; กลไก 4 ชั้น | knowledge, ARCHITECTURE |
| [[slot-layer-abstraction]] | Core เก็บ slot/layer แทนพิกัด; ตรวจด้วยชื่อ member | knowledge, ARCHITECTURE |
| [[layering]] | Presentation → Core ทางเดียว; UI ไม่แตะ `WorldState` | knowledge, ARCHITECTURE |
| [[magic-numbers]] | หลัง Stage 2 ตัวเลข tuneable ทั้งหมดอยู่ใน CSV | knowledge, ARCHITECTURE |
| [[data-integrity]] | foreign key ผิดทำให้ build พัง; id หายจะถูก quarantine | knowledge, ARCHITECTURE |
| [[reason-code]] | Core คืน `CommandReason` + args ไม่ใช่ประโยค | knowledge, ARCHITECTURE |
| [[out-of-scope-bodies]] | body ที่ยังไม่ทำต้อง throw พร้อม `// TODO(stage-N):` | knowledge |
| [[stage-order]] | Stage 1→6 ห้ามสลับ, 8 ก่อน 9; สถานะที่ยืนยันแล้ว | knowledge, README |
| [[scope-discipline]] | เปลี่ยนน้อยที่สุดที่ถูกต้อง, รายงานตรงไปตรงมา | knowledge |

### design — เรื่อง game design

| หน้า | สรุปหนึ่งบรรทัด | แหล่งที่มา |
|---|---|---|
| [[design/project-vision]] | ภาพรวม + สูตร Idol Manager × Fallout Shelter × XCOM | GDD 1.1 |
| [[design/core-fantasy]] | Handler — ส่งใครไปตายและอยู่กับผลนั้น | GDD 1.2 |
| [[design/core-loop]] | day cycle → mission → heat → กลับสู่ day cycle | GDD 1.3 |
| [[design/time-and-calendar]] | tick model, สัปดาห์, auto-pause, 4 Act | GDD 1.4 |
| [[design/agents]] | stats 7 ตัว, traits, วงจรชีวิต, permadeath | GDD 1.5 |
| [[design/base-building]] | ห้อง 6 หมวด + กฎ merge/depth/upkeep | GDD 1.6 |
| [[design/mission-and-fog-of-war]] ⭐ | node graph, fog ผูก Infiltration, Alarm 0-100 | GDD 1.7 |
| [[design/heat-and-counter-intel]] | Heat เพิ่ม/ลด, base defense, ต้นทุนของการสืบ | GDD 1.8 |
| [[design/progression]] | 4 Act + 3 endings + endless | GDD 1.9 |
| [[design/art-direction]] | 640×360, 48 สี, pixel art side-view cutaway | GDD 1.10 |

### queries — คำตอบและบทวิเคราะห์ที่ฟิลกลับเข้า wiki

_ยังว่าง_

## ข้อขัดแย้งที่พบ (ยังไม่แก้)

ดูรายละเอียดที่ [[sources/GDD]] — สรุป: ส่วนที่ 2 ของ `raw/GDD.md` ฝังสำเนา `knowledge.md` **รุ่นก่อน** ที่ระบุ RNG stream เพียง 3 ตัว (ขัดกับกฎที่บังคับใช้ซึ่งระบุ 5) และกำหนด save model ที่ไม่มีในกฎ — **`raw/knowledge.md` ชนะ ไม่มีการแก้ไฟล์ใดใน `raw/`**

## หน้าไฟล์พิเศษ

- [[log]] — บันทึกตามเวลา (append-only) ของทุก ingest / query / lint
- `../AGENTS.md` — schema และกฎการดูแล wiki
- `../raw/knowledge.md` — **กฎที่บังคับใช้ทั้งหมด** (อ่านก่อนเสมอ)

## ช่องว่างที่ยังรู้

| ช่องว่าง | หมายถึง |
|---|---|
| `ICommand` ไม่มี tick cost | กฎข้อ 6/10 ยังไม่ครบใน implementation; มาพร้อม Stage 4 |
| grep audit + Roslyn analyzer | งานของ Stage 5 ที่ยังไม่เริ่ม |
| save format จริง | `WorldStateSerializer` เป็นแค่ canonical dump; save คืองาน Stage 5 |
| mission generation | `MissionProgress` เป็น seam ว่าง; Stage 4 |
| `src/ProjectF.Tables` | ไดเรกทอรีเกมตกปลารุ่นก่อน ประกาศ namespace ชนกับของจริง — ควรลบแต่ต้องให้ผู้ใช้ตัดสินใจ |
| `docs/SAVE-FORMAT.md`, `docs/BALANCE.md` | อ้างถึงใน README แต่ไม่มีไฟล์ |
| unordered collection iteration | ห้ามใน GDD แต่ไม่มีในกฎและไม่มี test |

## รูปแบบหน้า

```markdown
---
type: source | system | concept | design | query
source: raw/<ไฟล์ที่มาของหน้านี้>
updated: YYYY-MM-DD
tags: [cipher]
---

# ชื่อหน้า

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md)

(เนื้อหาเป็นภาษาไทย — ศัพท์เทคนิคคงเป็นภาษาอังกฤษ — เชื่อมโยงด้วย [[wikilinks]])
```
