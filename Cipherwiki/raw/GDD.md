---

# ส่วนที่ 1: Game Design Document

## 1.1 ภาพรวม

| หัวข้อ | รายละเอียด |
|---|---|
| ชื่อโค้ด | **Project CIPHER** (ยังไม่ตั้งชื่อจริง) |
| แนวเกม | Spy Agency Management Sim + Tactical Infiltration |
| อ้างอิงหลัก | Idol Manager (บริหาร/ตารางเวลา/ดราม่า) × Fallout Shelter (ฐานตัดขวาง/ส่งคนออกภารกิจ) × XCOM (ความเสี่ยงถาวร) |
| แพลตฟอร์ม | PC (Steam) — Windows ก่อน, Linux/Proton ตามมา |
| Engine | Unity 2022.3 LTS (URP 2D) |
| ผู้เล่น | Single-player ล้วน ไม่มี network ใดๆ |
| สไตล์ภาพ | Pixel art, side-view cutaway |
| ความยาว | Campaign 20-30 ชม. + Endless sandbox |
| ราคาเป้าหมาย | $19.99 |
| ทีม | เล็ก (1-3 คน) → ต้องเป็นเกม **UI-driven** ไม่ใช่ content-heavy |

## 1.2 Core Fantasy

> คุณคือ **Handler** — ไม่เคยออกสนามเอง คุณนั่งอยู่หลังจอ สร้างฐาน ฝึกคน เลือกว่าจะส่งใครไปตาย และต้องอยู่กับผลของการตัดสินใจนั้น

ความรู้สึกเป้าหมาย: **"ฉันรู้ว่าส่งเขาไปน่าจะไม่รอด แต่ฉันไม่มีคนอื่นแล้ว"**

## 1.3 Core Loop

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

## 1.4 ระบบที่ 1: Time & Calendar

| กลไก | รายละเอียด |
|---|---|
| หน่วยเวลา | **Tick** (1 tick = 1 ชั่วโมงในเกม), 24 tick = 1 วัน |
| ความเร็ว | Pause / 1x / 2x / 4x — กดหยุดได้ตลอด (single-player ทำได้เต็มที่) |
| Auto-pause | ตั้งค่าได้ว่าจะหยุดอัตโนมัติเมื่อ: ภารกิจจบ, สปายบาดเจ็บ, เงินติดลบ, มี event ใหม่ |
| สัปดาห์ | 7 วัน = 1 สัปดาห์ → หักเงินเดือน + ค่า upkeep (จุดกดดันทางการเงินหลัก) |
| ฤดูกาล/ช่วง | 4 Act ตามเนื้อเรื่อง, แต่ละ Act ปลดล็อกศัตรูและห้องใหม่ |

**เหตุผลที่เลือก tick-based ไม่ใช่ real-time ต่อเนื่อง:** ทำให้ทุกอย่างเป็น integer, deterministic, เซฟ/โหลดง่าย, ทดสอบอัตโนมัติได้ และ balance sim รันได้เร็วมาก

## 1.5 ระบบที่ 2: Agents (สปาย)

### Stat

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

### Traits (นิสัย — คือตัวสร้างเรื่องเล่า)

สุ่ม 1-3 อันต่อคน บางอันซ่อนจนกว่าจะเปิดเผยตัวเอง

| ประเภท | ตัวอย่าง |
|---|---|
| ดี | Cold Blooded (Mental dmg -30%), Ghost (Infiltration +15), Quick Study (exp +25%) |
| เสีย | Claustrophobic (ในห้องปิด Nerve -20), Trigger Happy (ปะทะแล้ว Alarm +เพิ่ม), Gossip (ลด Loyalty คนรอบข้าง) |
| **ซ่อน** | **Mole** (ไส้ศึก — ส่งข้อมูลให้ศัตรู Heat เพิ่มเงียบๆ), Deserter (Loyalty ต่ำแล้วหนีกลางภารกิจ) |

**Mole คือหัวใจความ dark ของเกม** — ผู้เล่นจะเริ่มระแวงสปายของตัวเอง มีห้อง Counter-Intel ไว้สืบ แต่สืบผิดคนก็เสีย Loyalty ทั้งองค์กร

### วงจรชีวิต

```
Recruit → Train → Deploy → [Injured | Burnout | Captured | Dead | Veteran]
                                ↓            ↓          ↓
                           Infirmary    Dorm/Therapy  ภารกิจช่วยตัวประกัน
```

- **ตายถาวร** ไม่มีชุบชีวิต
- **ถูกจับ** → ศัตรูรีดข้อมูล → Heat พุ่ง → มีเวลา N วันไปช่วย ก่อนจะเสียถาวร
- **Burnout** (Mental = 0) → ปฏิเสธคำสั่ง, Loyalty ตก, อาจลาออกเอง

## 1.6 ระบบที่ 3: Base Building

Grid แบบ side-view cutaway (อ้างอิงภาพ Idol Manager/Fallout Shelter ที่คุณแนบ)

| หมวด | ห้อง | หน้าที่ |
|---|---|---|
| **ฝึก** | Gym, Range, Server Room, Social Lab, Isolation Chamber | ฝึก Physical/Combat/Tech/Social/Nerve |
| **ฟื้นฟู** | Dorm, Infirmary, Lounge, Therapy Office | ฟื้น PS/MS, ลด Burnout |
| **ปฏิบัติการ** | Ops Center (เพิ่ม slot ภารกิจพร้อมกัน), Comms Room (ลด Heat), Intel Archive (แปลง Intel เป็นข้อมูลภารกิจ) |
| **สนับสนุน** | Workshop (คราฟต์ gadget), Armory (เก็บ/อัปเกรดอุปกรณ์), Cafeteria (บัฟรายวัน) |
| **ป้องกัน** | Security Post, Vault, Safe Room, Decoy Front (ลดโอกาสโดนบุก) |
| **บริหาร** | Office (ลดเงินเดือน), HR (เพิ่มคุณภาพผู้สมัคร), Counter-Intel (สืบหา Mole) |

**กฎที่ทำให้เลย์เอาต์มีความหมาย:**
- ห้องชนิดเดียวกันติดกันในแนวนอน = รวมเป็นห้องใหญ่ (ประสิทธิภาพ/คนทำงานเพิ่ม) — เหมือน Fallout Shelter
- ยิ่งลึกลงใต้ดิน = ปลอดภัยจากการบุกมากขึ้น แต่ค่าก่อสร้างแพงขึ้น
- ห้องกินไฟ/กินเงิน upkeep ต่อสัปดาห์ → **สร้างเยอะเกินตัว = ล้มละลาย**

## 1.7 ระบบที่ 4: Mission & Fog of War ⭐

**นี่คือระบบที่ต้องออกแบบให้ดีที่สุด เพราะเป็นจุดขายของเกม**

### โครงสร้างแผนที่ (Node-based Procedural)

```
         [Vent]──[Hall]──[Lab]
           │       │       │
[ENTRY]──[Lobby]──[Sec]──[OBJECTIVE]──[Server]
           │       │                     │
         [Stair]─[Garage]──────────[EXTRACTION]
```

- สร้างด้วย **layered graph**: Entry layer → Transit layers → Objective → Extraction
- การันตีว่ามีเส้นทางถึง Objective เสมอ และมีทางเลือกอย่างน้อย 2 เส้น
- แต่ละ node มี: `RoomType`, `SecurityLevel`, `Contents` (loot/guard/trap/terminal), `NoiseLevel`

### Fog of War — ผูกกับ Infiltration

| สถานะ node | เงื่อนไข |
|---|---|
| **Hidden** (ไม่เห็นเลย) | ไกลเกินรัศมี |
| **Silhouette** (เห็นว่ามีห้อง แต่ไม่รู้มีอะไร) | ในรัศมี `Infiltration / 25` |
| **Scouted** (เห็นประเภทห้อง + ระดับ security) | ในรัศมี `Infiltration / 40` หรือใช้ gadget/แฮ็กกล้อง |
| **Revealed** (เห็นทุกอย่าง) | เคยเข้าไปแล้ว |

> **สูตรนี้คือแกนการเติบโตทั้งเกม:** Infiltration 20 = เห็นแค่ห้องติดกัน (เดินเดาสุ่ม เครียดมาก) → Infiltration 90 = เห็นล่วงหน้า 3 ห้อง วางแผนเส้นทางได้ (รู้สึกเป็นมืออาชีพ) **ความสนุกคือการได้เห็นมากขึ้นเรื่อยๆ**

### การ Resolve แต่ละ node

```
เข้า node → roll event จาก mission_event ตาม RoomType + SecurityLevel
          → skill check: d100 + AgentStat + GadgetBonus + TeamSupport
                         vs  DifficultyClass(node)
          → ผลลัพธ์ 4 ระดับ:
             Critical Success → ผ่านเงียบ + loot พิเศษ
             Success          → ผ่าน, Alarm +0~5
             Partial           → ผ่าน แต่ Alarm +10~20, PS/MS -
             Failure           → Alarm +25, บาดเจ็บ, อาจติดอยู่ในห้อง
```

### Alarm Meter (0-100) — ตัวสร้างความกดดัน

| ระดับ | ผล |
|---|---|
| 0-25 Calm | ปกติ |
| 26-50 Suspicious | ยามเดินตรวจเพิ่ม, DC +10 |
| 51-75 Alert | ปิดประตูบางบาน, เส้นทางหาย, ศัตรูไล่ล่า |
| 76-99 Lockdown | Extraction ปิดบางจุด, ต้องสู้ออก |
| 100 Burned | **บังคับจบภารกิจ** — ใครยังไม่ถึง Extraction เสี่ยงถูกจับ/ตาย |

### คำสั่งที่ผู้เล่นแทรกได้ระหว่างภารกิจ

`เดินต่อ` · `รอเงียบๆ (ลด Alarm, เปลือง tick)` · `ใช้ gadget` · `แยกทีม` · `บังคับเปิดประตู` · `ถอนตัวทันที`

## 1.8 ระบบที่ 5: Heat & Counter-Intelligence

| กลไก | รายละเอียด |
|---|---|
| Heat เพิ่มจาก | ภารกิจเสียงดัง, สปายถูกจับ, Mole ส่งข้อมูล, ทำงานซ้ำพื้นที่เดิม |
| Heat ลดจาก | Comms Room, เว้นช่วงไม่ทำงาน, ภารกิจ "ลบร่องรอย", ติดสินบน |
| Heat สูง | ราคางานดีขึ้น (ชื่อเสียง) แต่ → **ศัตรูบุกฐาน** (Base Defense event) |
| Base Defense | ต่อสู้ในฐานด้วยสปายที่อยู่บ้าน — ของจริงที่ทำให้ "ห้อง Security ไม่ใช่ของประดับ" |

## 1.9 Progression & Meta

| ชั้น | เนื้อหา |
|---|---|
| **Act 1** (วัน 1-30) | ฐานเล็ก 3 ห้อง, สปาย 2 คน, ภารกิจ Tier 1, สอนระบบ |
| **Act 2** (31-90) | ปลดล็อก Workshop/Counter-Intel, Mole คนแรกโผล่, คู่แข่งปรากฏ |
| **Act 3** (91-180) | ภารกิจ Tier 3-4, ศัตรูบุกฐาน, ตัวเลือกศีลธรรมหนักๆ |
| **Act 4** (181+) | เปิดโปงองค์กรเบื้องหลัง, 3 endings ตาม Loyalty/Heat/Intel |
| **Endless** | หลังจบ campaign เล่นต่อแบบไม่มีที่สิ้นสุด + leaderboard local |

## 1.10 Art Direction

| รายการ | สเปก |
|---|---|
| Internal resolution | 640×360 (upscale เป็น 1280×720 / 1920×1080) |
| Room module | 96×64 px ต่อ 1 ช่องห้อง |
| Agent sprite | 16×32 px, idle/walk/work/combat/hurt |
| Palette | 48 สี โทน desaturated + accent สีแสบ (แดง alarm, เขียว terminal) |
| UI | หนัก — panel ซ้าย roster, grid กลาง, bar บน (เลียนแบบเลย์เอาต์ Idol Manager ที่คุณแนบ) |
| Mission scene | โทนมืด, Fog เป็นพื้นที่ดำ/น้ำเงินเข้ม, ไฟฉายเรืองรอบตัวสปาย |

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

## 1.12 สถาปัตยกรรมหลัก ⭐

**หลักการเดียวที่สำคัญที่สุดของโปรเจกต์นี้:**

> แยก **Simulation Core** (C# ล้วน ไม่มี `using UnityEngine`) ออกจาก **Presentation** (Unity) อย่างเด็ดขาด

```
ProjectSpy.Core/        ← ไม่มี UnityEngine แม้แต่บรรทัดเดียว
  World, Calendar, Agent, Room, Mission, Resolver, RNG
       ↑ อ่าน/สั่งผ่าน command + event
Unity Presentation/        ← แค่วาดภาพกับรับ input
```

ได้อะไรกลับมา 4 อย่าง:

| ประโยชน์ | ทำไมคุ้ม |
|---|---|
| **ทดสอบได้ด้วย xUnit** | รัน test 1000 เคสใน 2 วินาที ไม่ต้องเปิด Unity |
| **Balance sim แบบ headless** | จำลอง 500 วัน × 100 ครั้ง ภายในไม่กี่นาที → หา balance ที่พังเจอตั้งแต่ก่อนปล่อย |
| **Save/Load ชัวร์** | เซฟแค่ `WorldState` ก้อนเดียว ไม่ต้องไล่เก็บจาก MonoBehaviour |
| **Replay/Bug report** | ผู้เล่นส่ง seed + command log มา → คุณ reproduce ได้เป๊ะ |

**กฎ determinism ยังอยู่** (แต่เหตุผลเปลี่ยน): ห้าม `DateTime.Now`, `UnityEngine.Random`, `Dictionary` enumeration ใน Core — ใช้ seeded RNG + `SortedDictionary` เพื่อให้ **เซฟ/โหลด/replay/sim ตรงกันเสมอ**

---

# ส่วนที่ 2: Stage Prompts 1-10

## เตรียมก่อน: `knowledge.md`

````markdown
# Project CIPHER — Knowledge

## What this is
A single-player PC game for Steam: a spy agency management simulator.
Base building and staff management in the style of Idol Manager, combined with
dispatched infiltration missions in the style of Fallout Shelter, with permadeath
consequences in the style of XCOM. Pixel art, side-view cutaway base.

NOT multiplayer. NOT networked. NOT blockchain. No server of any kind.

## The one architectural rule that matters most
`ProjectSpy.Core` is a pure C# simulation library.
- It MUST NOT reference UnityEngine, UnityEditor, or any Unity package. Ever.
- All game rules live there: time, agents, rooms, resources, missions, resolution.
- Unity is only a renderer and an input device. Presentation code may read Core state
  and send Core commands; it may never contain a game rule.
- If a rule has to be duplicated in Unity to make the UI work, that is a bug — expose it
  from Core instead.

## Determinism rules for Core (needed for save/load, replay and the balance simulator)
FORBIDDEN inside ProjectSpy.Core:
  DateTime.Now / UtcNow, System.Random, UnityEngine.Random, Guid.NewGuid(),
  file or network I/O, Environment.*, float accumulation for anything that affects rules,
  iteration over unordered Dictionary/HashSet when the order can affect outcomes.
REQUIRED instead:
  - A seeded `IRng` passed explicitly; separate streams per subsystem
    (WorldRng, MissionRng, EventRng) so adding a new roll in one system does not shift another.
  - Integer or fixed-point maths for all rule-affecting values. Floats are for rendering only.
  - SortedDictionary / sorted key iteration anywhere order could matter.
  - Time measured in Ticks (1 tick = 1 in-game hour, 24 ticks = 1 day), never in seconds.

## Time model
- Tick = 1 in-game hour. Day = 24 ticks. Week = 7 days (payday + upkeep).
- The player may pause and run at 1x / 2x / 4x. Speed affects only how fast real seconds
  map to ticks; it NEVER changes simulation results.
- Auto-pause triggers are user-configurable and must never be skipped silently.

## Save model
- One `WorldState` object serialized with MessagePack, plus a header containing
  schema version, build version, seed and playtime.
- Saves must survive data-table edits: unknown ids are reported and quarantined,
  never silently dropped. Every save migration gets a numbered migration step and a test.
- Autosave every in-game day, rotating 5 slots, plus a manual slot and an ironman slot.

## Content pipeline
- All tuneable content lives in CSV under `data/` and is compiled by Luban into
  `ProjectSpy.Tables`. No magic numbers in code — if a designer might change it, it is a row.
- Art is placeholder (generated flat-colour sprites) until late. Swapping in real pixel art
  must never require a code change.

## Tech stack (fixed)
Unity 2022.3 LTS (URP 2D) · VContainer · UniTask · R3 · MessagePack (saves) ·
Luban (CSV tables) · Steamworks.NET · xUnit (Core tests) · Unity Test Framework (Unity tests)

## Style
- C# file-scoped namespaces, nullable enabled, `sealed` by default.
- Core: no partial classes, no statics holding mutable state, no singletons.
- Unity: MVP — `XxxView` is a MonoBehaviour with zero logic; `XxxPresenter` is plain C#
  resolved by VContainer. One LifetimeScope per scene, parented to RootLifetimeScope.
- Comments and commit messages in English. Player-facing strings always via the
  localization table, never hard-coded.
````

---