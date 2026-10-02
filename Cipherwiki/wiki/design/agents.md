---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, agents, traits]
---

# Agents — สปาย, stats, traits และวงจรชีวิต

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.5)
> **อัปเดต:** 2026-10-02

## Stat

| ประเภท | Stat | ใช้ทำอะไร |
|---|---|---|
| **ทรัพยากร** | Physical Stamina (0-100) | ลดจากการเดินทาง/ต่อสู้/บาดเจ็บ ฟื้นด้วยการพัก |
| | Mental Stamina (0-100) | ลดจากความเครียด/เห็นคนตาย/โกหกนาน ฟื้นช้ากว่ามาก |
| **ทักษะ** | Infiltration | **กำหนดรัศมี Fog of War** + ผ่านด่านแบบไม่ถูกจับได้ |
| | Combat | ปะทะศัตรู, คุ้มกันทีม |
| | Tech | แฮ็ก, ปลดล็อก, ปลดชนวนกับดัก |
| | Social | ปลอมตัว, โน้มน้าว, ล้วงข้อมูลจาก NPC |
| | Nerve | ต้านทาน Mental damage, ลด Alarm ที่เกิดจากแพนิก |
| **ซ่อน** | Loyalty (0-100) | ต่ำ → ลาออก/ขายข้อมูล/เป็นไส้ศึก — **ผู้เล่นเห็นเป็นช่วงคร่าวๆ ไม่ใช่ตัวเลข** |

**ข้อสังเกตเรื่อง Infiltration:** stat เดียวที่ควบคุม fog of war ดู [[design/mission-and-fog-of-war]] — GDD เรียกมันว่า "แกนการเติบโตทั้งเกม" เพราะมันเปลี่ยน *คุณภาพของข้อมูล* ที่ผู้เล่นเห็น ไม่ใช่แค่ค่าผ่านด่าน

**ข้อสังเกตเรื่อง Loyalty:** ค่าเป็น 0-100 จริงใน Core แต่**ผู้เล่นเห็นเป็นช่วง** (เช่น "ดูซื่อ ๆ" / "น่าจะไม่ซื่อสัตย์") — เป็นการตัดสินใจของ Presentation ที่จะไม่แสดงตัวเลข ดู [[layering]]

## Traits — ตัวสร้างเรื่องเล่า

สุ่ม 1-3 อันต่อคน บางอันซ่อนจนกว่าจะเปิดเผยตัวเอง

| ประเภท | ตัวอย่าง |
|---|---|
| ดี | Cold Blooded (Mental dmg -30%), Ghost (Infiltration +15), Quick Study (exp +25%) |
| เสีย | Claustrophobic (ในห้องปิด Nerve -20), Trigger Happy (ปะทะแล้ว Alarm +เพิ่ม), Gossip (ลด Loyalty คนรอบข้าง) |
| **ซ่อน** | **Mole** (ไส้ศึก — ส่งข้อมูลให้ศัตรู Heat เพิ่มเงียบๆ), Deserter (Loyalty ต่ำแล้วหนีกลางภารกิจ) |

## วงจรชีวิต

```
Recruit → Train → Deploy → [Injured | Burnout | Captured | Dead | Veteran]
                                ↓            ↓          ↓
                           Infirmary    Dorm/Therapy  ภารกิจช่วยตัวประกัน
```

- **ตายถาวร** ไม่มีชุบชีวิต
- **ถูกจับ** → ศัตรูรีดข้อมูล → Heat พุ่ง → มีเวลา N วันไปช่วย ก่อนจะเสียถาวร
- **Burnout** (Mental = 0) → ปฏิเสธคำสั่ง, Loyalty ตก, อาจลาออกเอง

## Mole

**Mole คือหัวใจความ dark ของเกม** — ผู้เล่นจะเริ่มระแวงสปายของตัวเอง มีห้อง Counter-Intel ไว้สืบ แต่**สืบผิดคนก็เสีย Loyalty ทั้งองค์กร**

รายละเอียดเชิงลึกอยู่ใน `raw/doc/MOLE_DESIGN.md` (ยังไม่ได้ ingest) ดู [[design/heat-and-counter-intel]]

## เชื่อมโยง

- [[design/core-fantasy]] — permadeath คือ stake ของ Handler
- [[design/mission-and-fog-of-war]] — Infiltration กำหนดรัศมี fog; Burnout จาก Mental
- [[design/base-building]] — ห้องฝึก (Gym, Range, Server Room, Social Lab, Isolation) และห้องฟื้น (Dorm, Infirmary, Lounge, Therapy)
- [[design/heat-and-counter-intel]] — Mole เพิ่ม Heat เงียบๆ
- [[systems/tick-pipeline]] — Training, Recovery, StatusDecay คือ phase ที่ขับ stat เหล่านี้
- [[systems/irng-streams]] — trait สุ่มด้วย `Trait` stream, recruit ด้วย `Recruit` stream
- [[magic-numbers]] — ค่า -30%, +15, +25%, 0-100 ทั้งหมดเป็น tuneable ใน CSV
- [[reason-code]] — "ปฏิเสธคำสั่ง" ตอน Burnout ต้องคืน ReasonCode
- [[sources/GDD]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] Loyalty "แสดงเป็นช่วง" — ช่วงกว้างแค่ไหน และใช้เกณฑ์อะไรตัด
- [ ] "มีเวลา N วันไปช่วย" หลังถูกจับ — N เท่าไร และถ้าพลาดจะเป็น permanent อย่างไร
- [ ] Veteran ได้มาทางไหน และให้อะไร (ยังไม่มีกลไก exp ใน GDD)
