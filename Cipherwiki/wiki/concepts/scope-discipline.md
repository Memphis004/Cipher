---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, process, code-style]
---

# Scope Discipline

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 9)
> **อัปเดต:** 2026-10-02

- **เปลี่ยนน้อยที่สุดที่ยังถูกต้อง** (smallest correct change)
- **เขียนตาม convention ที่มีอยู่แล้วในไฟล์ที่กำลังแก้**
- **ห้าม refactor code นอก blast radius ของงาน**

## รายงานตรงไปตรงมา

**รายงานข้อค้นพบอย่างตรงไปตรงมา** — ทั้ง design review ของ Stage 6 และ review ของ Stage 10 **ขอความตรงไปตรงมามากกว่าความสบายใจ** นี่เป็นกฎที่ใช้กับการรายงาน ไม่ใช่แค่กับโค้ด

## เชื่อมโยง

- [[out-of-scope-bodies]] — `// TODO(stage-N):` คือการ scope งานที่ยังไม่ทำ
- [[stage-order]] — ลำดับ stage ที่ห้ามสลับเป็นขอบเขตที่แคบลงตามลำดับ
- [[core-purity]] — การแก้โค้ดที่ทำให้ Core ขยะ pure ไม่ใช่ scope creep แต่เป็นการละเมิดกฎ
- [[data-integrity]] — กฎข้อ 9
- [[sources/knowledge]] — กฎข้อ 9

## หมายเหตุ

กฎนี้ใช้กับ wiki ด้วย — หน้าใหม่ควรมีเฉพาะเนื้อหาที่ยังไม่มีหน้าไหนครอบคลุม การเพิ่มหน้าที่ซ้ำซ้อนเป็น scope creep ของ wiki
