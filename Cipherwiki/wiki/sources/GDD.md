---
type: source
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, gdd, design]
---

# GDD.md — Game Design Document

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md)
> **อัปเดต:** 2026-10-02

สรุปหนึ่งย่อหน้า: GDD แบ่งเป็นสองส่วน ส่วนที่ 1 คือ design document 12 หัวข้อ (ภาพรวม, core fantasy, core loop, time, agents, base building, mission & fog of war, heat, progression, art direction, tech stack, architecture) ส่วนที่ 2 คือ **stage prompts 1–10 ซึ่งฝังสำเนา `knowledge.md` รุ่นก่อนไว้ในไฟล์ด้วย**

## ⚠️ ข้อขัดแย้ง: ส่วนที่ 2 ฝัง `knowledge.md` รุ่นเก่า

> **ขัดแย้งกับ raw/knowledge.md — flag ไว้ ยังไม่แก้**
>
> `raw/GDD.md` ส่วนที่ 2 ("เตรียมก่อน: `knowledge.md`") ฝังข้อความกฎชุดหนึ่งที่**ขัดแย้งกับกฎที่บังคับใช้** ใน `raw/knowledge.md` อย่างเจาะจง:

| ประเด็น | `raw/knowledge.md` (authoritative) | สำเนาใน GDD.md |
|---|---|---|
| RNG streams | **ห้า** named streams: World, Mission, Event, Recruit, Trait | **สาม**: `WorldRng, MissionRng, EventRng` |
| การห้ามใน Core | ไม่ได้ห้าม unordered collection | ห้าม "iteration over unordered `Dictionary`/`HashSet` when the order can affect outcomes" + ต้องใช้ `SortedDictionary` |
| Save model | ไม่ได้ระบุ MessagePack | กำหนด MessagePack + autosave ทุกวัน 5 slots + migration step |

**ไม่พบข้อขัดแย้งที่เป็นเรื่องเนื้อหา** — ทั้งสองฉบับตรงกันเรื่อง Unity boundary (`ProjectSpy.Core` ห้าม reference `UnityEngine`/`UnityEditor`/Unity package ใดๆ), tick model (1 tick = 1 ชั่วโมง, 24 tick = 1 วัน, 7 วัน = 1 สัปดาห์), integer/fixed-point ใน rule paths, seeded `IRng` ที่ส่งเข้ามาอย่างชัดเจน, ห้าม `DateTime.Now`, `System.Random`, `Guid.NewGuid`, I/O, `Environment.*`, และ "no magic numbers — ถ้า designer อาจเปลี่ยนมัน มันคือหนึ่งแถว"

**วิธีจัดการ:** `raw/knowledge.md` ชนะเสมอ ตาม `AGENTS.md` ข้อ 2.2 ไม่มีการแก้ไฟล์ใด ๆ ใน `raw/` หน้านี้บันทึกข้อขัดแย้งไว้เพื่อให้เห็น และทุกหน้าที่เกี่ยวกับ RNG ใน wiki ใช้ **ห้า streams** ตามกฎที่บังคับ

**ข้อสังเกตที่สนับสนุนการตัดสินนั้น:** [[systems/irng-streams]] ระบุว่า `RngStreams` derive **ห้า** generator จาก world seed และ `Trait` stream มีอยู่จริง เหตุผลที่ระบุคือ "การเพิ่ม roll ในการสร้าง trait หนึ่งครั้งจะเลื่อนทุก mission map หลังจากนั้น" ซึ่งตรงกับเหตุผลของกฎข้อ 6 ใน `raw/knowledge.md` — ฉบับที่ฝังใน GDD.md เป็นรุ่นก่อนที่ยังไม่มี Recruit/Trait stream

## โครงสร้างส่วนที่ 1

| หัวข้อ | เนื้อหา | หน้า |
|---|---|---|
| 1.1 | ภาพรวมโครงการ | [[design/project-vision]] |
| 1.2 | Core Fantasy — ตัว Handler | [[design/core-fantasy]] |
| 1.3 | Core Loop — day cycle / mission / heat | [[design/core-loop]] |
| 1.4 | Time & Calendar — tick model | [[design/time-and-calendar]] |
| 1.5 | Agents — stats, traits, วงจรชีวิต | [[design/agents]] |
| 1.6 | Base Building — ห้องทั้ง 6 หมวด | [[design/base-building]] |
| 1.7 | Mission & Fog of War ⭐ | [[design/mission-and-fog-of-war]] |
| 1.8 | Heat & Counter-Intelligence | [[design/heat-and-counter-intel]] |
| 1.9 | Progression — 4 Act + Endless | [[design/progression]] |
| 1.10 | Art Direction | [[design/art-direction]] |
| 1.11 | Tech Stack | [[systems/tech-stack]] |
| 1.12 | สถาปัตยกรรมหลัก | [[systems/tech-stack]] |

## เชื่อมโยง

- [[sources/knowledge]] — กฎที่ GDD ต้องยึด
- [[sources/ARCHITECTURE]] — รายละเอียดการ implement ของสถาปัตยกรรมใน 1.12 ซึ่งยืนยันว่า stream มีห้าตัว
- [[design/mission-and-fog-of-war]] — ระบบที่ GDD ระบุว่า "ต้องออกแบบให้ดีที่สุด เพราะเป็นจุดขายของเกม" ยังไม่เริ่ม (Stage 4)
- [[slot-layer-abstraction]] — GDD 1.6 กล่าวถึง "grid แบบ side-view cutaway" ซึ่งเป็นภาพของ presentation ไม่ใช่พิกัดใน Core

## คำถามที่ยังไม่มีคำตอบ

- [ ] สำเนา `knowledge.md` ที่ฝังใน GDD.md ควรถูกลบออกจาก GDD.md หรือไม่ — เป็นเอกสารที่ตัดสินใจเองไม่ได้ ต้องให้ผู้ใช้ตัดสินใจ
- [ ] ตัวเลขใน GDD (Infiltration/25, Alarm 0-100, Act 1 = วัน 1-30, 48 สี, 96×64 px) เป็น design intent หรือค่าที่ต้องย้ายเข้า CSV — ดู [[magic-numbers]]
