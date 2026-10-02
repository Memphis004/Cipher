---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, progression, acts]
---

# Progression & Meta — 4 Act และ Endless

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.9)
> **อัปเดต:** 2026-10-02

| ชั้น | เนื้อหา |
|---|---|
| **Act 1** (วัน 1-30) | ฐานเล็ก 3 ห้อง, สปาย 2 คน, ภารกิจ Tier 1, สอนระบบ |
| **Act 2** (31-90) | ปลดล็อก Workshop/Counter-Intel, Mole คนแรกโผล่, คู่แข่งปรากฏ |
| **Act 3** (91-180) | ภารกิจ Tier 3-4, ศัตรูบุกฐาน, ตัวเลือกศีลธรรมหนักๆ |
| **Act 4** (181+) | เปิดโปงองค์กรเบื้องหลัง, 3 endings ตาม Loyalty/Heat/Intel |
| **Endless** | หลังจบ campaign เล่นต่อแบบไม่มีที่สิ้นสุด + leaderboard local |

## Act boundary เป็น progression แบบไม่ใช้เลขสถิติ

Act ขับเคลื่อนด้วย **จำนวนวัน** ไม่ใช่ด้วย exp — สอดคล้องกับ progression ของ fog of war ใน [[design/mission-and-fog-of-war]] ที่ Infiltration สูงขึ้นเพราะ*เห็น*มากขึ้น ไม่ใช่เพราะตัวเลขใหญ่ขึ้น

**Act 2 เป็นจุดพลิกของเรื่องมืด:** Mole คนแรกโผล่พร้อมกับ Workshop/Counter-Intel ที่ใช้สืบมัน — ผู้เล่นได้เครื่องมือตามปัญหาพอดี ดู [[design/agents]]

**Act 3 เป็น Act ที่ตรวจสอบว่าผู้เล่นเข้าใจระบบหรือไม่:** ศัตรูบุกฐาน (จาก Heat สูง) บังคับให้ห้องป้องกันมีความหมาย ดู [[design/heat-and-counter-intel]]

## 3 endings

จัดตาม **Loyalty / Heat / Intel** — ทั้งสามค่าเป็นสถิติที่ติดตามมาตลอดเกม ไม่ใช่ค่าที่ผู้เล่นเลือกปลายทาง

| ตัวแปร | สถานะที่เก็บ | ความหมาย |
|---|---|---|
| Loyalty | ซ่อนจากผู้เล่น (แสดงเป็นช่วง) | องค์กรยังอยู่กับ Handler หรือไม่ ดู [[design/agents]] |
| Heat | มองเห็นได้ | ระดับความเสี่ยงสะสมของทั้งองค์กร |
| Intel | เก็บใน Intel Archive | ความรู้ที่สืบมาได้ |

> **ข้อสังเกต:** Loyalty ถูกซ่อนจากผู้เล่นแต่เป็นตัวกำหนด ending — นี่คือแนวคิดเดียวกับการไม่แสดงตัวเลข Loyalty ใน roster ผู้เล่นจึงอ่าน ending ได้โดยไม่ได้อ่านค่าตรงๆ เป็นการออกแบบที่ผู้เล่นต้องเดาอยู่จริง

## Endless + leaderboard local

`Steamworks.NET` รองรับ stats (ตาม [[systems/tech-stack]]) แต่ leaderboard เป็น **local** — สอดคล้องกับกฎข้อ 4: ไม่มี network ใดๆ

## เชื่อมโยง

- [[design/mission-and-fog-of-war]] — Tier 1 ถึง Tier 4 ของภารกิจ และ fog ที่กว้างขึ้นเมื่อ Infiltration สูงขึ้น
- [[design/agents]] — Mole คนแรกโผล่ใน Act 2
- [[design/heat-and-counter-intel]] — ศัตรูบุกฐานใน Act 3 คือผลของ Heat
- [[design/base-building]] — Act 1 เริ่มด้วยฐาน 3 ห้อง
- [[design/time-and-calendar]] — 4 Act ตามจังหวะวัน
- [[sources/GDD]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] Mission Tier 1-4 แตกต่างกันที่อะไร — จำนวน node, SecurityLevel, หรือ DifficultyClass
- [ ] "คู่แข่งปรากฏ" ใน Act 2 เป็น NPC ที่แข่งอย่างไร แล้วกระทบ Heat หรือราคางานอย่างไร
- [ ] เกณฑ์ที่เลือก ending จาก Loyalty/Heat/Intel คืออะไร และแสดงให้ผู้เล่นเห็นก่อนจบแค่ไหน
- [ ] Act 4 "เปิดโปงองค์กรเบื้องหลัง" คืออะไร — กลไกใหม่ หรือส่วนที่เล่าไม่ได้ตอนต้น
