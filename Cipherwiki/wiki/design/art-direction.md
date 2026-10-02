---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, art, pixel-art]
---

# Art Direction

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.10)
> **อัปเดต:** 2026-10-02

| รายการ | สเปก |
|---|---|
| Internal resolution | 640×360 (upscale เป็น 1280×720 / 1920×1080) |
| Room module | 96×64 px ต่อ 1 ช่องห้อง |
| Agent sprite | 16×32 px, idle/walk/work/combat/hurt |
| Palette | 48 สี โทน desaturated + accent สีแสบ (แดง alarm, เขียว terminal) |
| UI | หนัก — panel ซ้าย roster, grid กลาง, bar บน (เลียนแบบเลย์เอาต์ Idol Manager) |
| Mission scene | โทนมืด, Fog เป็นพื้นที่ดำ/น้ำเงินเข้ม, ไฟฉายเรองรอบตัวสปาย |

## ข้อสังเกตเชิงสถาปัตยกรรม

สเปกภาพทั้งหมดนี้เป็น **เรื่องของ Presentation** — ไม่มีค่าไหนในตารางข้างบนที่ Core ต้องรู้

โดยเฉพาะ **Room module 96×64 px ต่อ 1 ช่องห้อง** ต้องไม่กลายเป็นขนาดห้องใน Core: ตามกฎข้อ 10 ([[slot-layer-abstraction]]) Core เก็บ `SlotCount` และ `Layer` แล้ว **Presentation ตัดสินว่า slot หนึ่งหน่วยแมปเป็น 2D, 2.5D หรือ 3D อย่างไร** ดู [[systems/grid-to-slot-migration]]

หมายเหตุว่า "ช่องห้อง" ใน GDD คือคำที่คนออกแบบภาพใช้ ไม่ใช่ชื่อ type ใน Core

**Fog เป็นพื้นที่ดำ/น้ำเงินเข้ม** สอดคล้องกับ [[design/mission-and-fog-of-war]] — ผู้เล่นเห็นสถานะ `Hidden` เป็นสีที่มองไม่ออก ส่วน `Scouted`/`Revealed` คือสิ่งที่ Core เก็บเป็นข้อมูลแล้ว

## Art เป็น placeholder จนถึงช่วงท้าย

> **เป็นการอนุมาน:** `knowledge.md` ที่ฝังอยู่ใน GDD.md ระบุว่า "Art is placeholder (generated flat-colour sprites) until late. Swapping in real pixel art must never require a code change." ถ้อยคำนี้สอดคล้องกับกฎข้อ 3 ([[magic-numbers]]) — สเปกภาพจึงควรอยู่ในตารางหรือ asset config ไม่ใช่ค่าที่ hard-code ใน Unity code
>
> **ยังไม่ตัดสินใจ:** ข้อความนี้อยู่ในสำเนา `knowledge.md` รุ่นเก่าที่ฝังใน GDD.md ซึ่งขัดแย้งกับกฎที่บังคับใช้บางจุด — ดู [[sources/GDD]] ก่อนนำมาใช้เป็นกฎ

## เชื่อมโยง

- [[design/project-vision]] — pixel art, side-view cutaway เป็นสไตล์ที่เลือก
- [[systems/tech-stack]] — Unity 2022.3 LTS (URP 2D)
- [[slot-layer-abstraction]] — Presentation ตัดสิน mapping ระหว่าง slot กับตำแหน่งจริง
- [[design/base-building]] — ห้อง 6 หมวดคือสิ่งที่ต้องมี room module
- [[stage-order]] — art สำคัญหลัง simulation ตามข้อ 1
- [[sources/GDD]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] 48 สี กำหนดไว้ใน palette asset หรือ Unity project settings
- [ ] Agent sprite 5 สถานะ (idle/walk/work/combat/hurt) ครอบคลุมสถานะใน Core ครบหรือไม่ — เช่น ตอนถูกจับหรือ burnout
