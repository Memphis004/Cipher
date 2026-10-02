---
type: design
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, design, loop]
---

# Core Loop — วงจรหลักของเกม

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.3)
> **อัปเดต:** 2026-10-02

```
┌─ DAY CYCLE (บริหาร) ──────────────────────────┐
│  จัดสปายเข้าห้องฝึก / พักฟื้น / ประจำการ        │
│  ดู Contract Board รับงานใหม่                   │
│  สร้าง/อัปเกรดห้อง  ·  ซื้อ gadget  ·  รับสมัครคน │
│  จ่ายเงินเดือน + ค่าดูแลห้อง (ทุกสิ้นสัปดาห์)      │
└───────────────┬──────────────────────────────┘
                │ ส่งทีมออกภารกิจ
                ▼
┌─ MISSION (แทรกซึม) ───────────────────────────┐
│  แผนที่ node สุ่ม + Fog of War                  │
│  เดินทีละห้อง → skill check → Alarm ขยับ         │
│  ผู้เล่นแทรกคำสั่ง: ใช้ gadget / เปลี่ยนเส้นทาง /  │
│  ถอนตัว / ลุยต่อ                                │
│  ทำภารกิจหลัก → ไปจุด Extraction                │
└───────────────┬──────────────────────────────┘
                │ ผลลัพธ์
                ▼
   เงิน + Intel + ของ + Exp  ·  บาดเจ็บ/Burnout/ถูกจับ
                │
                ▼
   Heat เพิ่ม → ศัตรูสืบจนเจอฐาน → โดนบุก
                │
                ▼
          กลับไป DAY CYCLE
```

## โครงสร้าง

วงจรเป็น **สองโหมดที่สลับกัน** ไม่ใช่ real-time ต่อเนื่อง — ผู้เล่นอยู่ใน day cycle (บริหาร) แล้ว "ส่งทีมออกภารกิจ" เพื่อเข้าสู่ mission (แทรกซึม) แล้วผลลัพธ์จะป้อนกลับเข้า day cycle

**Day cycle** เป็นจุดที่ระบบเศรษฐกิจกดดัน: จ่ายเงินเดือน + upkeep ทุกสัปดาห์ ดู [[design/time-and-calendar]] และ [[design/base-building]]

**Mission** คือจุดที่ permadeath เกิดขึ้น — ดู [[design/mission-and-fog-of-war]]

**Heat** เป็นตัวเชื่อมระหว่างสองโหมด: ภารกิจที่ทำไปแล้วยังส่งผลต่อ day cycle ถัดไป ดู [[design/heat-and-counter-intel]]

## ตัวเลขใน loop ที่เป็น design intent

ตัวเลขทั้งหมดใน loop นี้ (ค่าบางอย่าง, สัปดาห์, ระดับ Alarm) เป็น **ค่าที่ต้องอยู่ใน CSV** ตาม [[magic-numbers]] ไม่ควร hard-code ใน Core

## เชื่อมโยง

- [[design/core-fantasy]] — Handler คือคนที่ขับวงจรนี้
- [[design/time-and-calendar]] — tick model ที่ให้ day cycle มีจังหวะ
- [[design/mission-and-fog-of-war]] — ระบบที่ GDD ระบุว่าเป็นจุดขายของเกม
- [[design/heat-and-counter-intel]] — ผลลัพธ์ของ mission ที่ย้อนกลับมากดดัน day cycle
- [[design/progression]] — Act ที่ปลดล็อกเนื้อหาใหม่ในวงจร
- [[systems/tick-pipeline]] — กลไกใน Core ที่จะขับเคลื่อน day cycle
