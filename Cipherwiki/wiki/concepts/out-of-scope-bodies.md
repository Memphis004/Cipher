---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, core, stages, code-style]
---

# Out-of-scope bodies ต้อง explicit

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 5)
> **อัปเดต:** 2026-10-02

body ที่อยู่นอกขอบเขตของ stage ปัจจุบัน ต้อง **throw `NotImplementedException`** และต้องมี comment **`// TODO(stage-N):`** ที่ระบุ stage ที่จะมาทำให้จบ

**ห้ามปล่อย method body ที่ว่างเงียบ** — เหตุผลที่กฎให้คำเตือนตรงนี้: *"ความเงียบถูกอ่านว่า 'ทำเสร็จแล้ว'"* method ที่ว่างจะผ่านการทดสอบ ผ่านการ review และหลุดลอดไปจนกว่าจะมีคนเรียกมัน

## ทำไมต้องมีชื่อ stage ใน comment

เพราะมันเปลี่ยน method ที่ยังไม่ทำให้เป็น **รายการงานที่มีเจ้าของและกำหนดเวลา** แทนที่จะเป็นข้อผิดพลาดที่ไม่มีใครรู้ว่าจะกลับมาทำเมื่อไร ตรงกับ [[stage-order]] ที่กำหนดลำดับไว้แล้ว

## เชื่อมโยง

- [[stage-order]] — `// TODO(stage-N):` ผูก body ที่ยังไม่ทำเข้ากับลำดับ stage
- [[tick-pipeline]] — ตัวอย่างที่เป็นไปได้: phase ที่ยังไม่ได้ implement ใน stage ปัจจุบัน
- [[scope-discipline]] — กฎข้อ 9 ที่ควบคุมการแก้ไขไฟล์
- [[sources/knowledge]] — กฎข้อ 5

## คำถามที่ยังไม่มีคำตอบ

- [ ] มี test ที่ assert ว่าไม่มี empty body หลงเหลืออยู่ใน Core หรือไม่
