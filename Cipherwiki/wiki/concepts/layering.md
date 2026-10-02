---
type: concept
source: raw/knowledge.md
updated: 2026-10-02
tags: [cipher, layering, architecture, core]
---

# Layering — Presentation → Core ทางเดียว

> **แหล่งที่มา:** [raw/knowledge.md](../../raw/knowledge.md) (ข้อ 8)
> **อัปเดต:** 2026-10-02

**Presentation** (Unity, Stage 7+) เป็นเจ้าของ input, rendering, audio และ localization มัน **อ่าน** Core และ **สั่ง command** ให้ Core — **ไม่มีวัน mutate `WorldState` โดยตรง**

ทุกการเปลี่ยนแปลงของโลกเดินผ่าน **`ICommand.Execute`** ดู [[command-log]]

## ขอบเขตของแต่ละชั้น

| | Presentation (Unity, Stage 7+) | Core (netstandard2.1) |
|---|---|---|
| Input | เป็นเจ้าของ | ไม่รับ input |
| Rendering / audio / localization | เป็นเจ้าของ | ไม่มี |
| `WorldState` | อ่านอย่างเดียว | mutate ได้ผ่าน `ICommand.Execute` เท่านั้น |
| ตำแหน่งบนจอ | ตัดสินใจเอง | ไม่รู้จัก ดู [[slot-layer-abstraction]] |
| ข้อความให้ผู้เล่น | localize เอง | คืน key ดู [[reason-code]] |

## ขอบเขตที่ยืนยันแล้วจากการ implement

| กลไก | หน้าที่ |
|---|---|
| `GameSession.Execute` | ทางเข้าเดียวของ mutation ดู [[systems/game-session]] |
| `Validate` / `Apply` แยกกัน | UI เรียก `Validate` ทำ ghost preview ได้โดยไม่มี side effect |
| **double-validate + throw** | จับ command ที่ไม่ deterministic ซึ่งจะทำให้ log เสีย |
| `GameEvent` + pending buffer | Core สื่อ "เกิดอะไรขึ้น" โดยไม่ให้ถูกแก้ ดู [[systems/game-event-buffer]] |
| `Core` / `Tables` / `Sim` / Unity | สาม assembly โหลด Core.dll เดียวกัน ดู [[project-layout]] |

## เชื่อมโยง

- [[core-purity]] — ข้อห้ามระดับ assembly ที่ทำให้ชั้นแยกได้จริง
- [[command-log]] — ช่องทางเดียวของ mutation
- [[determinism]] — เหตุผลที่ Core ต้องบริสุทธิ์พอจะรัน headless
- [[stage-order]] — Unity เข้ามาใน Stage 7
- [[slot-layer-abstraction]] — Presentation เป็นผู้ map slot เป็นตำแหน่งจริง
- [[reason-code]] — Presentation map code เป็นข้อความ
- [[sources/knowledge]] — กฎข้อ 8
- [[systems/tech-stack]] — VContainer, UniTask, R3 ที่ Presentation ใช้
- [[design/project-vision]] — "UI-driven ไม่ใช่ content-heavy" คือเหตุผลเชิงสถาปัตยกรรมของกฎนี้

## คำถามที่ยังไม่มีคำตอบ

- [ ] Unity อ่าน `WorldState` ผ่าน view layer ตรง ๆ หรือผ่าน DTO/projection ที่แปลงแล้ว — ยังตอบไม่ได้เพราะ Unity ยังไม่เริ่ม (Stage 7)
- [ ] MVP pattern ที่สำเนา `knowledge.md` ระบุ (`XxxView` เป็น MonoBehaviour ที่ไม่มี logic, `XxxPresenter` เป็น plain C#) ยังใช้อยู่หรือถูกแทนด้วย R3 ดู [[systems/tech-stack]]
