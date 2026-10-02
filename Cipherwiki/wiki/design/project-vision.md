---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, vision]
---

# Project Vision — ภาพรวมโครงการ

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.1)
> **อัปเดต:** 2026-10-02

| หัวข้อ | รายละเอียด |
|---|---|
| ชื่อโค้ด | **Project CIPHER** (ยังไม่ตั้งชื่อจริง) |
| แนวเกม | Spy Agency Management Sim + Tactical Infiltration |
| อ้างอิงหลัก | Idol Manager (บริหาร/ตารางเวลา/ดราม่า) × Fallout Shelter (ฐานตัดขวาง/ส่งคนออกภารกิจ) × XCOM (ความเสี่ยงถาวร) |
| แพลตฟอร์ม | PC (Steam) — Windows ก่อน, Linux/Proton ตามมา |
| Engine | Unity 2022.3 LTS (URP 2D) |
| ผู้เล่น | Single-player ล้วน ไม่มี network ใดๆ |
| สไตล์ภาพ | Pixel art, side-view cutaway |
| ความยาว | Campaign 20-30 ชม. + Endless sandbox |
| ราคาเป้าหมาย | $19.99 |
| ทีม | เล็ก (1-3 คน) → ต้องเป็นเกม **UI-driven** ไม่ใช่ content-heavy |

## ข้อสรุปเชิงออกแบบที่สำคัญที่สุด

**ทีม 1-3 คน → เกมต้องเป็น UI-driven ไม่ใช่ content-heavy**

นี่ไม่ใช่แค่ข้อจำกัดด้านทรัพยากร แต่เป็น **ข้อกำหนดเชิงสถาปัตยกรรม** ที่สอดคล้องกับ [[layering]] และ [[reason-code]] พอดี:

- UI-driven แปลว่าตรรกะอยู่ใน Core และ UI เป็นแค่การแสดงผล — ไม่ต้องเขียน logic ซ้ำใน Unity ซึ่งคือกฎข้อ 8
- UI-driven ทำให้ Core ทดสอบได้ด้วย xUnit โดยไม่ต้องเปิด Unity — ตาม [[systems/tech-stack]]
- ทุกอย่างที่ Core คืนให้ผู้เล่นต้องเป็น key และ id ไม่ใช่ prose — ตาม [[reason-code]] ทำให้ localize ได้โดยไม่ต้องแตะ Core

## สูตรของการอ้างอิงสามเกม

การอ้างอิงไม่ได้แค่บอก "เหมือนเกมนี้" แต่บอกว่า **แต่ละเกมคุมมิติไหน**:

| เกมอ้างอิง | มิติที่ยืมมา | หน้าใน wiki |
|---|---|---|
| Idol Manager | การบริหาร, ตารางเวลา, ดราม่า, เลย์เอาต์ roster + grid | [[design/base-building]], [[design/core-loop]] |
| Fallout Shelter | ฐานตัดขวาง, ส่งคนออกภารกิจ, การ merge ห้อง | [[design/base-building]] |
| XCOM | ความเสี่ยงถาวร (permadeath) | [[design/agents]], [[design/core-fantasy]] |

การรวมสามเกมนี้ทำให้เกมเป็น **management sim ที่มี tactical layer** ไม่ใช่เกม tactical ล้วน — ผู้เล่น 90% เวลาอยู่ใน day cycle

## ชื่อและความยาว

- **"Project CIPHER" ยังไม่ตั้งชื่อจริง** — ชื่อจริงอาจเปลี่ยนภายหลัง หน้านี้จึงอ้างถึงโครงการเสมอด้วยชื่อทั้งสอง (Project CIPHER / ProjectSpy คือโครงการเดียวกัน)
- Campaign 20-30 ชม. — สอดคล้องกับ 4 Act ของ [[design/progression]] (วัน 1-181+ โดย ~1 วันเกม = ~5-10 นาทีจริง)

## เชื่อมโยง

- [[design/core-fantasy]] — Handler คือตัวละครผู้เล่น
- [[design/progression]] — 4 Act ที่ประกอบกันเป็น 20-30 ชั่วโมง
- [[systems/tech-stack]] — Unity 2022.3 LTS และเครื่องมือทั้งหมด
- [[design/art-direction]] — pixel art, side-view cutaway
- [[stage-order]] — simulation-first มาก่อน art ตามกฎข้อ 1
- [[sources/GDD]] — หน้าต้นทาง
