---
type: system
source: raw/GDD.md
updated: 2026-10-02
tags: [cipher, tech-stack, unity, architecture]
---

# Tech Stack และสถาปัตยกรรมหลัก

> **แหล่งที่มา:** [raw/GDD.md](../../raw/GDD.md) (ข้อ 1.11 และ 1.12)
> **อัปเดต:** 2026-10-02

## 1.11 Tech Stack (ปรับใหม่หมด)

| Tool | ใช้ทำอะไร | เปลี่ยนจากเดิมยังไง |
|---|---|---|
| **Unity 2022.3 LTS (URP 2D)** | Engine | เหมือนเดิม |
| **VContainer** | DI | ✅ เก็บไว้ |
| **UniTask** | Async | ✅ เก็บไว้ |
| **Luban** | CSV → C# data tables | ✅ เก็บไว้ (สำคัญกว่าเดิมอีก เพราะ content เยอะ) |
| **MessagePack** | **ไฟล์เซฟ** (ไม่ใช่ network แล้ว) | 🔄 เปลี่ยนบทบาท |
| **R3** (Reactive) | binding state → UI | ➕ เพิ่ม — เกม UI หนักต้องใช้ |
| **Steamworks.NET** | achievement, cloud save, stats | ➕ เพิ่ม |
| ~~Libplanet~~ · ~~MagicOnion~~ · ~~Protobuf~~ | — | ❌ **ตัดทิ้งหมด** |

**การตัด blockchain ทิ้ง** สอดคล้องกับคำสั่งชัดเจนใน `knowledge.md` ฉบับฝัง: "NOT multiplayer. NOT networked. NOT blockchain. No server of any kind."

## 1.12 สถาปัตยกรรมหลัก ⭐

> หลักการเดียวที่สำคัญที่สุดของโปรเจกต์นี้:
> **แยก Simulation Core (C# ล้วน ไม่มี `using UnityEngine`) ออกจาก Presentation (Unity) อย่างเด็ดขาด**

```
ProjectSpy.Core/        ← ไม่มี UnityEngine แม้แต่บรรทัดเดียว
  World, Calendar, Agent, Room, Mission, Resolver, RNG
       ↑ อ่าน/สั่งผ่าน command + event
Unity Presentation/        ← แค่วาดภาพกับรบับ input
```

### ได้อะไรกลับมา 4 อย่าง

| ประโยชน์ | ทำไมคุ้ม |
|---|---|
| **ทดสอบได้ด้วย xUnit** | รัน test 1000 เคสใน 2 วินาที ไม่ต้องเปิด Unity |
| **Balance sim แบบ headless** | จำลอง 500 วัน × 100 ครั้ง ภายในไม่กี่นาที → หา balance ที่พังเจอตั้งแต่ก่อนปล่อย |
| **Save/Load ชัวร์** | เซฟแค่ `WorldState` ก้อนเดียว ไม่ต้องไล่เก็บจาก MonoBehaviour |
| **Replay/Bug report** | ผู้เล่นส่ง seed + command log มา → คุณ reproduce ได้เป๊ะ |

## กฎ determinism ที่ GDD ระบุ

> ห้าม `DateTime.Now`, `UnityEngine.Random`, `Dictionary` enumeration ใน Core — ใช้ seeded RNG + `SortedDictionary` เพื่อให้ **เซฟ/โหลด/replay/sim ตรงกันเสมอ**

**หมายเหตุ:** ประโยคนี้อยู่ในส่วนที่ 1 ซึ่งเป็น design intent ปัจจุบัน ไม่ใช่ในสำเนา `knowledge.md` รุ่นเก่า — แต่ข้อ "ห้าม unordered `Dictionary`/`HashSet` enumeration" **ไม่ปรากฏใน `raw/knowledge.md`** ดู [[sources/GDD]] สำหรับข้อขัดแย้งทั้งหมด

`raw/doc/ARCHITECTURE.md` กล่าวถึงเรื่องนี้ในฐานะ determinism concern แต่ไม่ได้บังคับใช้เป็นกฎ — เป็นช่องว่างระหว่าง design doc กับกฎที่บังคับใช้

## เชื่อมโยง

- [[layering]] — กฎข้อ 8 ที่ GDD 1.12 ขยายความ
- [[core-purity]] — ข้อ 2 ที่ GDD เรียกว่าหลักการที่สำคัญที่สุดของโปรเจกต์
- [[slot-layer-abstraction]] — ข้อ 10 ที่แยก Core ออกจาก Presentation ในอีกมิติ
- [[systems/simulation-rules]] — Luban → `ProjectSpy.Tables` → Core
- [[systems/game-session]] — "command + event" ใน diagram ของ GDD
- [[systems/tech-stack]] — `ProjectSpy.Sim` (headless) และ `ProjectSpy.Tables` ที่ GDD ไม่ได้แสดงใน diagram
- [[determinism]] — ประโยชน์ 4 ข้อคือผลทางปฏิบัติของกฎข้อ 6
- [[stage-order]] — Stage 6 คือ headless playability, Stage 7 คือ Unity
- [[sources/ARCHITECTURE]] — diagram ฉบับละเอียดกว่านี้
- [[sources/GDD]] — หน้าต้นทาง

## คำถามที่ยังไม่มีคำตอบ

- [ ] MessagePack ถูกใช้กับ save แล้ว — แต่ `ARCHITECTURE.md` ระบุว่า `WorldStateSerializer` เป็นแค่ canonical dump สำหรับเทียบ byte ไม่ใช่ save file จริง (Stage 5 ยังไม่เริ่ม) ดู [[systems/world-state]]
- [ ] Cloud save ผ่าน Steamworks.NET จะมีผลกับ determinism อย่างไร ถ้า save ของผู้เล่นถูก sync ข้ามเวอร์ชันเกม
- [ ] R3 ใช้กับ state จาก Core อย่างไร โดยไม่ทำให้ Presentation กลายเป็นแหล่งของ state
