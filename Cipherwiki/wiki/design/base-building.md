---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, base, rooms, economy]
---

# Base Building — ห้องทั้งหมดและกฎเลย์เอาต์

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.6)
> **อัปเดต:** 2026-10-02

Grid แบบ side-view cutaway (อ้างอิงภาพ Idol Manager / Fallout Shelter)

## ห้อง 6 หมวด

| หมวด | ห้อง | หน้าที่ |
|---|---|---|
| **ฝึก** | Gym, Range, Server Room, Social Lab, Isolation Chamber | ฝึก Physical/Combat/Tech/Social/Nerve |
| **ฟื้นฟู** | Dorm, Infirmary, Lounge, Therapy Office | ฟื้น PS/MS, ลด Burnout |
| **ปฏิบัติการ** | Ops Center (เพิ่ม slot ภารกิจพร้อมกัน), Comms Room (ลด Heat), Intel Archive (แปลง Intel เป็นข้อมูลภารกิจ) |
| **สนับสนุน** | Workshop (คราฟต์ gadget), Armory (เก็บ/อัปเกรดอุปกรณ์), Cafeteria (บัฟรายวัน) |
| **ป้องกัน** | Security Post, Vault, Safe Room, Decoy Front (ลดโอกาสโดนบุก) |
| **บริหาร** | Office (ลดเงินเดือน), HR (เพิ่มคุณภาพผู้สมัคร), Counter-Intel (สืบหา Mole) |

## กฎที่ทำให้เลย์เอาต์มีความหมาย

- **ห้องชนิดเดียวกันติดกันในแนวนอน = รวมเป็นห้องใหญ่** (ประสิทธิภาพ/คนทำงานเพิ่ม) — เหมือน Fallout Shelter
- **ยิ่งลึกลงใต้ดิน = ปลอดภัยจากการบุกมากขึ้น แต่ค่าก่อสร้างแพงขึ้น**
- **ห้องกินไฟ/กินเงิน upkeep ต่อสัปดาห์ → สร้างเยอะเกินตัว = ล้มละลาย**

## Layer depth เป็นกฎ ไม่ใช่แค่ภาพ

`raw/doc/ARCHITECTURE.md` ยืนยันว่า **Layer ถูกรักษาไว้โดยเจตนา** เพราะ depth เป็น rule input — ห้องที่อยู่ layer ลึกกว่ามีต้นทุนก่อสร้างสูงกว่า (`ApplyDepthCost`) และ merge-adjacency ก็ยังอยู่ เพราะห้องอาจถูกกำหนดให้แชร์ layer ซึ่งกันไม่ให้ห้องกลืนห้องที่วางอยู่ด้านบน ดู [[systems/grid-to-slot-migration]]

> **ข้อควรระวัง:** คำว่า "grid" ใน GDD เป็นภาพของผู้เล่น ไม่ใช่พิกัดที่ Core เก็บ — Core เก็บ `SlotIndices` + `Layer` ตามกฎข้อ 10 ([[slot-layer-abstraction]]) "ติดกันในแนวนอน" ใน Core คือ `AdjacentRoomIds` ที่เป็น explicit data และ Presentation เป็นผู้ตัดสินว่าห้องไหนติดกันจริง

## เศรษฐกิจ: จุดกดดันสองด้าน

| ดันขึ้น | ดันลง |
|---|---|
| upkeep ต่อห้องต่อสัปดาห์ | ค่าก่อสร้างสูงขึ้นตาม layer |
| เงินเดือน (สัปดาห์ละครั้ง) | Ops Center จำกัดจำนวนภารกิจพร้อมกัน |

ผลคือ **ต้องเลือกว่าจะขยายหรือเติบโตทีม** — ซึ่งคือ [[design/core-fantasy]]: Handler ไม่มีเงินพอทำทั้งสองอย่าง

## เชื่อมโยง

- [[design/agents]] — ห้องฝึก/ฟื้นหล่อนให้ stat และ status
- [[design/heat-and-counter-intel]] — Comms Room ลด Heat, Security Post/Vault/Safe Room/Decoy Front ป้องกันการบุก
- [[design/time-and-calendar]] — upkeep และเงินเดือนจ่ายทุกสัปดาห์
- [[slot-layer-abstraction]] — กฎข้อ 10 ที่บังคับให้ห้องอยู่ใน slot/layer
- [[systems/grid-to-slot-migration]] — การแปลง grid เป็น slot ใน Core
- [[systems/simulation-rules]] — ตัวเลขทั้งหมดมาจากตาราง
- [[design/mission-and-fog-of-war]] — Ops Center กำหนดจำนวนภารกิจพร้อมกัน
- [[sources/GDD]] — หน้าต้นทาง
