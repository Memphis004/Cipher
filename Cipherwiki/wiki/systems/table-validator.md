---
type: system
source: raw/doc/ARCHITECTURE.md
updated: 2026-10-02
tags: [cipher, testing, tables, data]
---

# TableValidator — บังคับใช้ข้อมูลด้วย test

> **แหล่งที่มา:** [raw/doc/ARCHITECTURE.md](../../raw/doc/ARCHITECTURE.md)
> **อัปเดต:** 2026-10-02

`TableValidator` รันเป็น test และตรวจ**มากกว่า**แค่ "id ไม่ซ้ำ" — มีกฎหลายข้อที่มีเพราะ **ตารางอาจสอดคล้องภายในตัวเองแต่ทำให้ระบบทำงานเงียบ ๆ**

## หลักการ

> "A balance error that **produces no error message** is the worst kind, so each one is caught at build time"

สอดคล้องกับกฎข้อ 7 ([[data-integrity]]) ที่บอกว่า "ตารางที่พังต้องไม่มีวันไปถึงสถานะที่เล่นได้"

## กฎที่ระบุไว้

### `ValidateCounterIntelBalance`

เคสต้อง **สรุปได้** (best case ผ่าน threshold) **และต้องไม่สรุปโดยเฉลี่ย** (expected case ไม่ผ่าน)

> "The second half is as important as the first: **evidence accrues identically whoever you investigate**, so a threshold below the expected case would have the desk **exposing people at random** and make the player's own correlation work worthless"

ดู [[systems/mole-system]]

### Recovery room ratio

การฟื้นฟู mental ต้อง**ช้ากว่า physical อย่างมีนัยสำคัญ** และ**ห้องเฉพาะทาง (therapy, infirmary) ถูกยกเว้น** จากการตรวจต่อห้อง เพื่อไม่ให้อัตราของห้องเฉพาะทางตัวเองไป trip check

> **เป็นการอนุมาน:** นี่ทำให้ตัวเลขที่ผู้เล่นเห็นเป็น design invariant ไม่ใช่แค่ค่าปรับแต่ง — Mental ฟื้นช้ากว่า Physical เป็นแกนของแรงกดดันทางอารมณ์ใน [[design/agents]]

### Required keys per rule table

**ทุก key ที่ระบบอ่านต้องมีอยู่** เพื่อไม่ให้ typo กลายเป็น fallback ที่ **ลบ penalty ไปอย่างเงียบงัน**

เชื่อมกับ [[systems/simulation-rules]]: fallback ที่เงียบงันคือกลไกที่อันตรายที่สุด และกฎนี้คือสิ่งที่ปิดมัน

### Id space disjointness

แต่ละ key-value table **มี id band ของตัวเอง** บังคับใช้ ไม่ใช่รักษาด้วยมือ

ดูคำถามที่ค้างใน [[systems/simulation-rules]] เรื่อง band ของแต่ละตาราง

## Missing data เป็น build failure ไม่ใช่ runtime crash

> "Missing data is a build failure (`TableValidator`), **not a runtime crash**."

ต่างจาก **save** ที่อาจอ้างถึง id ซึ่งถูกถอด — save ต้อง quarantine ([[data-integrity]]) แต่ตารางที่พังต้องทำให้ build พัง สองเส้นทางนี้ตั้งใจแยกจากกัน

## เชื่อมโยง

- [[data-integrity]] — กฎข้อ 7 ที่ test นี้คือกลไก
- [[magic-numbers]] — ทุกอย่างที่ validate คือ tuneable value
- [[systems/simulation-rules]] — accessor ที่อ่านค่าที่ผ่านการตรวจแล้ว
- [[systems/mole-system]] — counter-intel balance
- [[systems/determinism-tests]] — test อีกชุดที่พิสูจน์กฎข้อ 6
- [[design/agents]] — mental vs physical recovery
- [[out-of-scope-bodies]] — หลักการเดียวกัน: ความเงียบถูกอ่านว่าทำเสร็จ
- [[sources/ARCHITECTURE]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] Recovery ratio "meaningfully slower" คืออัตราส่วนเท่าไร และเกณฑ์ที่ test ใช้
- [ ] มีกฎ validate อื่นนอกเหนือจาก 4 ข้อที่เอกสารระบุหรือไม่ — `README.md` อ้างถึง "dangling foreign key, non-positive weight, missing localization key, unknown skill reference, mission tier with fewer than three usable node rooms" ซึ่งมากกว่า 4 ข้อนี้ ดู [[sources/README]]
- [ ] 5 ข้อใน README เป็นของเดิมที่ไม่ได้ระบุใน ARCHITECTURE หรือเป็นชื่อย่อของกฎเดียวกัน
- [ ] "mission tier with fewer than three usable node rooms" — อ้างถึงโครงสร้าง mission ที่ยังไม่ถูก generate (Stage 4) หรือไม่
