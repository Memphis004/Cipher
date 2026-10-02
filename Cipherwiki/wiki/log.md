---
type: log
updated: 2026-10-02
---

# log.md — บันทึกตามเวลา

ไฟล์นี้เป็น **append-only** ห้ามแก้หรือลบรายการเก่า
เพิ่มรายการใหม่ต่อท้ายเสมอ ใช้หัวข้อรูปแบบนี้เสมอ เพื่อให้ parse ด้วย unix tools ได้:

```
## [YYYY-MM-DD] <ประเภท> | <หัวข้อ>
```

`<ประเภท>` มีได้แค่: `ingest` | `query` | `lint` | `setup`

ดู 5 รายการล่าสุด:

```bash
grep "^## \[" wiki/log.md | tail -5
```

---

## [2026-10-02] setup | สร้างโครงสร้าง LLM Wiki

- สร้าง `wiki/` พร้อมหมวดหมู่: `sources`, `systems`, `concepts`, `design`, `queries`
- สร้าง `wiki/index.md` (content-oriented) และ `wiki/log.md` (chronological)
- เขียน schema ที่ `AGENTS.md` — กำหนดว่า `raw/` เป็น read-only, `raw/knowledge.md` เป็นกฎ authoritative, หน้า wiki เขียนเป็นภาษาไทยและต้องอ้างอิงไฟล์ต้นทางเสมอ
- **ยังไม่ได้ ingest อะไร** — รอผู้ใช้สั่ง

---

## [2026-10-02] ingest | raw/knowledge.md

อ่าน `raw/knowledge.md` ทั้งไฟล์ แตกเป็นหน้าตามกฎ 10 ข้อ แต่ละข้อได้หน้า concept หรือ system ของตัวเอง และหน้า source สรุปรวมที่ทำหน้าที่เป็นตารางกฎ

**สร้าง 14 หน้า:**

- `sources/knowledge.md` — สรุปรวม พร้อมตารางกฎ 10 ข้อที่ลิงก์ไปยังหน้าย่อย
- `concepts/stage-order.md`, `concepts/core-purity.md`, `concepts/magic-numbers.md`, `concepts/reason-code.md`, `concepts/out-of-scope-bodies.md`, `concepts/determinism.md`, `concepts/data-integrity.md`, `concepts/layering.md`, `concepts/scope-discipline.md`, `concepts/slot-layer-abstraction.md`
- `systems/command-log.md`, `systems/tick-pipeline.md`, `systems/irng-streams.md`

**อัปเดต:** `index.md` (สถานะ 0 → 14 หน้า, เติมตารางทุกหมวด, ระบุไฟล์ที่ยังไม่ได้ ingest)

**การเชื่อมโยง:** ข้อ 6 และ 10 เชื่อมกันสองทาง — determinism เป็นเหตุผลเชิงระบบของการไม่เก็บพิกัด และ command log คือกลไกที่ทำให้การเดินในห้องต้องเป็น command จึง replay ได้ ข้อ 2, 3, 4, 7, 8 อ้างถึงกันเป็นวง

**สิ่งที่พบระหว่างอ่าน:**

- กฎข้อ 3 (no magic numbers) มีผลบังคับใช้ตั้งแต่ Stage 2 ซึ่งเสร็จแล้ว ไม่ใช่ตอนถึง Stage 5 — บันทึกไว้ใน `concepts/magic-numbers.md`
- enforcement ของข้อ 2 และ 10 (`CorePurityTests`, Roslyn analyzer) ถูกผังไว้ใน Stage 5 ที่ยังไม่เริ่ม จึงยังไม่มีกลไกบังคับใช้จริงในตอนนี้ — บันทึกไว้ใน `concepts/stage-order.md`
- ตารางสถานะ stage ในหน้า `concepts/stage-order.md` มาจาก `raw/README.md` ซึ่งยังไม่ได้ ingest จึงติดป้าย `เป็นการอนุมาน` ไว้แล้ว
- `docs/SAVE-FORMAT.md` ถูกอ้างถึงใน `README.md` แต่ไม่มีไฟล์นี้ใน `raw/` — ยังยืนยันไม่ได้จนกว่าจะ ingest `README.md`

**ไม่พบข้อขัดแย้งกับ `raw/knowledge.md`** — ครั้งแรกที่ ingest กฎ authoritative ยังไม่มีคำสั่งอย่างอื่นมาขัดกัน

---

## [2026-10-02] ingest | raw/GDD.md

Game Design Document 22 KB — ส่วนที่ 1 เป็น design 12 หัวข้อ ส่วนที่ 2 เป็น stage prompts 1–10 ที่ฝังสำเนา `knowledge.md` รุ่นก่อนไว้

**สร้าง 11 หน้า:**

- `sources/GDD.md` — สรุป + ตารางข้อขัดแย้ง
- `design/project-vision.md`, `design/core-fantasy.md`, `design/core-loop.md`, `design/time-and-calendar.md`, `design/agents.md`, `design/base-building.md`, `design/mission-and-fog-of-war.md`, `design/heat-and-counter-intel.md`, `design/progression.md`, `design/art-direction.md`
- `systems/tech-stack.md`

**⚠️ พบข้อขัดแย้ง — flag แล้ว ไม่แก้:** ส่วนที่ 2 ของ GDD ฝัง `knowledge.md` รุ่นเก่าที่ระบุ RNG stream เพียง **สาม** ตัว (`WorldRng, MissionRng, EventRng`) ขัดกับกฎข้อ 6 ที่ระบุ **ห้า** ตัว — อีกจุดคือกฎรุ่นเก่าห้าม unordered collection enumeration และกำหนด save model (MessagePack + autosave 5 slots) ซึ่งไม่มีในกฎที่บังคับใช้

ตรวจสอบแล้วว่า**เนื้อหาส่วนอื่นตรงกัน**ทั้งหมด: Unity boundary, tick model, integer/fixed-point, seeded `IRng`, ห้าม `DateTime.Now`/`System.Random`/`Guid.NewGuid`/I/O/`Environment.*`, no magic numbers

**การตัดสินใจ:** ทุกหน้าเกี่ยวกับ RNG ใช้ห้า streams ตามกฎที่บังคับ; ไม่มีการแก้ไฟล์ใดใน `raw/`

---

## [2026-10-02] ingest | raw/doc/ARCHITECTURE.md

เอกสารสถาปัตยกรรม 22 KB ที่อธิบายกลไกจริง — นี่คือแหล่งที่ทำให้ concept pages มีรายละเอียด implementation

**สร้าง 12 หน้า:**

- `sources/ARCHITECTURE.md`
- `systems/game-session.md`, `systems/game-event-buffer.md`, `systems/game-clock.md`, `systems/replay.md`, `systems/determinism-tests.md`, `systems/world-state.md`, `systems/core-purity-tests.md`, `systems/grid-to-slot-migration.md`, `systems/grid-commands.md`, `systems/simulation-rules.md`, `systems/table-validator.md`, `systems/mole-system.md`, `systems/tick-order-tests.md`

**เขียนทับ:** `systems/tick-pipeline.md`, `systems/irng-streams.md` (เดิมสรุปจากกฎอย่างเดียว ตอนนี้มีกลไกจริงและเหตุผลของลำดับทุกคู่)

**คำถามที่ได้คำตอบ:** adjacency เก็บเป็น explicit symmetric data (`AdjacentRoomIds`) · `CorePurityTests` ตรวจได้ทั้ง type-level และ name-level · `RngStreams` derive ห้า generator · `ProjectSpy.Tables` เข้า Core ด้วย direct reference · slug ของแต่ละ stream

**สิ่งที่พบว่าเป็นช่องว่าง:** `ICommand` ยังไม่มี tick cost ตามที่เอกสารระบุตรง ๆ ว่าจะมาพร้อม Stage 4 — นี่เป็นกฎข้อ 6/10 ที่ยังไม่ครบใน implementation

**บทเรียนที่บันทึกไว้:** Stage 1 เคยมี replay log สองอันที่ทำให้ determinism test พิสูจน์อะไรไม่ได้เลย เพราะ divergence อยู่ใน harness ไม่ใช่ในเกม

---

## [2026-10-02] ingest | raw/README.md

ไฟล์สั้นที่สุด แต่ปิดช่องว่างหลายจุด

**สร้าง 2 หน้า:** `sources/README.md`, `project-layout.md`

**ยืนยันแล้ว:** ตารางสถานะ stage ใน `concepts/stage-order.md` ถูกติดป้าย `เป็นการอนุมาน` มาตลอด — ตอนนี้ยืนยันจาก README แล้วและเอาป้ายออก

**ข้อสังเกตสำคัญ:** README และ ARCHITECTURE.md อ้างถึง `TableValidator` ตัวเดียวกันแต่ให้รายการกฎที่ **ไม่ทับกันเลย** — README ให้ 5 ข้อ (dangling foreign key, non-positive weight, missing localization key, unknown skill reference, mission tier ที่มี node room ใช้ได้น้อยกว่าสาม) ส่วน ARCHITECTURE.md ให้ 4 ข้ออื่น รวมแล้วอย่างน้อย 9 กฎ — ต้องยืนยันกับโค้ดว่ากฎไหนอยู่จริง

**เรื่องที่ควรตัดสินใจ:** `src/ProjectF.Tables` เป็นไดเรกทอรีเกมตกปลารุ่นก่อนที่**ประกาศ namespace `ProjectSpy.Tables` ชนกับ assembly จริง** ถูกเอาออกจาก solution แล้วแต่ยังอยู่บนดิสก์ — ถ้ามีโค้ดอ้าง namespace นั้นโดยไม่ตั้งใจจะได้ค่ามั่วโดยไม่มี error ควรลบ แต่เป็นการเปลี่ยนโครงสร้าง repo จึงไม่ได้ทำ

**เอกสารที่อ้างถึงแต่ไม่มีไฟล์:** `docs/SAVE-FORMAT.md`, `docs/BALANCE.md`, `data/README.md`

---

## [2026-10-02] lint | หลัง ingest สามแหล่ง

**อัปเดต:** `index.md` (เขียนใหม่ทั้งไฟล์ — 42 หน้า, 4/7 sources, เพิ่มส่วน "ช่องว่างที่ยังรู้" และ "ข้อขัดแย้งที่พบ")

**อัปเดต concept pages ให้ตรงกับหลักฐานที่ใหม่กว่า:**

- `concepts/stage-order.md` — เอาป้าย "เป็นการอนุมาน" ออกจากตารางสถานะ, เพิ่มรายการที่ Stage 3 ส่งมอบ
- `concepts/determinism.md` — เพิ่มตารางกลไก 4 ชั้น, double-validate, counterweights, บทเรียน harness
- `concepts/core-purity.md` — ยืนยันว่า `CorePurityTests` มีแล้ว พร้อมข้อ "no third-party dependencies"
- `concepts/slot-layer-abstraction.md` — เพิ่มตาราง Was/Is และผลข้างเคียงเรื่อง adjacency
- `concepts/magic-numbers.md` — ตอบคำถามเรื่อง direct reference
- `concepts/reason-code.md` — ระบุชื่อ type จริง (`CommandReason` + `CommandArgs`) แทน `ReasonCode`
- `concepts/data-integrity.md` — แยก missing-data เป็น build failure กับ accessor ที่ tolerant ต่อ retired id
- `concepts/layering.md` — เพิ่มตารางกลไกที่ยืนยันแล้ว
- `systems/command-log.md` — เพิ่ม flow จริงและช่องว่างเรื่อง tick cost

**คำถามที่ปิด:** 3 ข้อ (adjacency, CorePurityTests, RNG stream count) · **คำถามใหม่ที่เพิ่ม:** ~20 ข้อ กระจายในหัวข้อ "คำถามที่ยังไม่มีคำตอบ" ของแต่ละหน้า

**หมวดที่ยังว่าง:** `queries/` ยังไม่มีหน้าเลย — ยังไม่เคยมี query ที่ถามแล้วฟิลกลับ
