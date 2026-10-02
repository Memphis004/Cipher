# AGENTS.md — Project CIPHER LLM Wiki schema

This file is the schema for the LLM Wiki in this vault. It tells any LLM agent how the
wiki is structured, what the conventions are, and what workflow to follow when ingesting
sources, answering questions, or maintaining the wiki.

The vault root is the game repository folder named `ProjectSpy`. The project is called
**Project CIPHER**. They are the same project — never treat them as two.

The wiki is just markdown in a git repo. Obsidian is the IDE; the LLM is the programmer;
the wiki is the codebase.

---

## 1. Architecture — three layers

| Layer | Location | Who writes it |
|---|---|---|
| **Raw sources** | `raw/` | The human. **Immutable. Read-only.** |
| **The wiki** | `wiki/` | The LLM. The LLM owns this layer entirely. |
| **The schema** | `AGENTS.md` (this file) | Human + LLM, co-evolved. |

---

## 2. Hard rules — these override everything

### 2.1 `raw/` is READ-ONLY

- **Never edit, move, rename, reformat, or delete anything inside `raw/`.**
- Only read from it. `raw/` is the source of truth; the wiki is a derived artifact.
- If a raw source is wrong, outdated, or contradicts itself: **do not fix it.** Write the
  wiki page as-is, then flag the problem to the user in your reply and in the page's
  `> **หมายเหตุ:**` block.
- New sources are added by the human dropping files in. The agent never adds to `raw/`.

### 2.2 `raw/knowledge.md` is the authoritative rule set

- `raw/knowledge.md` holds the binding rules for every stage of Project CIPHER. It
  outranks the wiki, and it outranks this schema file.
- **If a wiki page would conflict with `raw/knowledge.md`: STOP and flag the conflict to
  the user. Never override it. Never write the conflicting page.**
- Concretely, an agent must not:
  - restate a rule in a way that loosens, narrows, or contradicts it;
  - infer a design decision that `raw/knowledge.md` forbids (Core referencing
    `UnityEngine`, `System.Random`, `DateTime`, `Guid.NewGuid`, `File.`, `Environment.`,
    float arithmetic in rule paths, coordinates/meshes/camera data in Core, English prose
    from Core, magic numbers in Core after Stage 2, silently empty method bodies, stage
    reordering, or bypassing `ICommand.Execute` for mutation);
  - "fix" a violation it notices in a raw document by rewriting the rule.
- The correct response to a conflict is always: name the two statements, cite both file
  paths, and ask the user which one governs. Report findings plainly — bluntness over
  comfort is itself a rule (`raw/knowledge.md` §9).
- If a later brief or a new raw source contradicts `raw/knowledge.md`, that is exactly the
  case the rule anticipates: stop and ask before writing anything.

### 2.3 Language and linking conventions

- **Every wiki page is written in Thai.** Keep **technical terms in English** — do not
  translate type names, method names, enum values, file paths, stage names, or domain
  vocabulary. Write `determinism`, not "ดีเทอร์มินิซึม"; write `ICommand.Execute`,
  `ReasonCode`, `RoomContents`, `SlotIndex`, `TickPipeline` as-is.
- Use **`[[wikilinks]]`** for all internal links. First mention of a page should link it;
  later mentions may be plain text. Link to raw sources with relative markdown links
  (`[raw/knowledge.md](../raw/knowledge.md)`), since raw files live outside the wiki.
- Do not invent English prose that the game itself would show a player — that is a Core
  rule, and quoting player-facing strings from a raw source is fine but must be marked as
  a quote, not restated as a design intent.

### 2.4 Every page cites its source

- **Every wiki page must state which raw file(s) it came from.** No exceptions, including
  index and log entries.
- Put a `source:` field in the frontmatter **and** a visible `> **แหล่งที่มา:**` line at
  the top of the body. Both. The frontmatter is for Dataview; the visible line is for the
  human reading in Obsidian.
- If a page was written from a conversation rather than a file, its source must be
  `conversation` and the log entry must say so.
- Never write a claim into the wiki that no raw file supports. If it is an inference, mark
  it `> **เป็นการอนุมาน:**` and cite the raw lines it rests on.

---

## 3. Layout

```
<vault root>/
├── AGENTS.md              # this file — the schema
├── raw/                   # READ-ONLY source documents (human-owned)
│   ├── knowledge.md       # AUTHORITATIVE rule set — outranks the wiki
│   ├── GDD.md
│   ├── README.md
│   ├── prompt.md
│   └── doc/
│       ├── ARCHITECTURE.md
│       ├── FLOORPLAN_UI.md
│       └── MOLE_DESIGN.md
└── wiki/                  # LLM-owned
    ├── index.md           # content-oriented catalog
    ├── log.md             # chronological, append-only
    ├── sources/           # one summary page per raw file
    ├── systems/           # entities: classes, services, subsystems, projects, files
    ├── concepts/          # ideas, patterns, terminology, invariants
    ├── design/            # game design topics: economy, agents, missions, heat, meta
    └── queries/           # good answers filed back into the wiki
```

Category placement rules:

- **`sources/`** — exactly one page per file in `raw/`, named after the file
  (`MOLE_DESIGN.md` → `sources/MOLE_DESIGN.md`).
- **`systems/`** — entity pages: a class, a subsystem, a pipeline, a project, a data table
  set. "Entity" means a thing the project has.
- **`concepts/`** — abstractions and invariants: determinism, Core purity, tick ordering,
  the `SlotIndex`/`Layer` contract, `ReasonCode`.
- **`design/`** — game design subject matter: economy and bankruptcy ladder, training and
  burnout, loyalty drift, the mole and counter-intel, mission generation, fog of war.
- **`queries/`** — answers and analyses worth keeping: comparisons, investigations,
  connections discovered while answering a question.

If a page does not fit, pick the closest category and note why in the page. Do not create
new top-level categories without asking.

---

## 4. Page format

```markdown
---
type: source | system | concept | design | query
source: raw/doc/MOLE_DESIGN.md
updated: YYYY-MM-DD
tags: [cipher, mole]
---

# ชื่อหน้า (ภาษาไทย, technical terms เป็น English)

> **แหล่งที่มา:** [raw/doc/MOLE_DESIGN.md](../../raw/doc/MOLE_DESIGN.md)
> **อัปเดต:** 2026-10-02

สรุปหนึ่งย่อหน้า: หน้านี้คืออะไร ทำไมถึงสำคัญ

## รายละเอียด

(เนื้อหาเป็นภาษาไทย — [[wikilinks]] ไปยังหน้าที่เกี่ยวข้อง)

## เชื่อมโยง

- [[หน้าที่เกี่ยวข้อง]] — ความสัมพันธ์

## ข้อสังเกต / ข้อขัดแย้ง

> **ขัดแย้งกับ raw/knowledge.md:** ... (ถ้ามี — flag อย่างเดียว อย่าแก้)

## คำถามที่ยังไม่มีคำตอบ

- ...
```

Obsidian conventions:

- Link path is relative to the vault root for display purposes but written as plain
  `[[Page Name]]`; Obsidian resolves by filename. Keep filenames unique across `wiki/`.
- Use `- [ ]` task lists under `## คำถามที่ยังไม่มีคำตอบ` for open threads.
- Tags stay lowercase and English: `cipher`, `determinism`, `economy`, `mole`, `stage-3`.
- Dataview may query the frontmatter; keep `type`, `source`, `updated` present on every page.

---

## 5. index.md — content-oriented

- A catalog of everything in the wiki: each page with a link, a one-line Thai summary,
  and optionally `updated` or a source count.
- Organized by category, mirroring the `wiki/` subfolders.
- **Updated on every ingest and every filed query.** This is not optional bookkeeping.
- When answering a question, **read `index.md` first**, find the relevant pages, then drill
  into them. This works well at moderate scale (hundreds of pages) and needs no embeddings
  or vector search.
- Keep the category tables sorted by name within each category.

## 6. log.md — chronological, append-only

- Every entry starts with a consistent, parseable prefix:

  ```
  ## [YYYY-MM-DD] ingest | MOLE_DESIGN.md
  ## [YYYY-MM-DD] query | เปรียบเทียบโมลกับ loyalty drift
  ## [YYYY-MM-DD] lint | ตรวจหน้า orphan
  ```

- Allowed types: `ingest`, `query`, `lint`, `setup`.
- Each entry records: what changed, which pages were created/updated, and any conflict
  flagged to the user.
- **Append only.** Never rewrite or delete past entries. To correct something, add a new
  entry that says what was corrected.
- Recent history is greppable:

  ```bash
  grep "^## \[" wiki/log.md | tail -5
  ```

---

## 7. Operations

### Ingest

Triggered only when the user asks. **Do not ingest proactively.**

1. Read the raw source in full. If it references images or assets, read the text first,
   then view the referenced images separately.
2. Discuss the key takeaways with the user before writing, if they want to be involved.
3. Write `sources/<File>.md` — the summary page, with its citation.
4. Update `index.md`: add the source page and any new entity/concept pages it introduced.
5. Update existing pages across the wiki that the new source touches. A single source may
   touch 10–15 pages. Update, do not duplicate — if a page already covers the topic,
   revise it in place and note what changed.
6. Flag contradictions between the new source and existing wiki pages, in the pages
   themselves and in your reply.
7. Append one `ingest` entry to `log.md`.

**Before integrating, check the new source against `raw/knowledge.md`.** If it conflicts,
stop and flag it to the user before writing pages that would encode the conflict.

### Query

1. Read `index.md` to find candidate pages.
2. Read those pages. Cite them by filename in the answer.
3. Answer in Thai with English technical terms, matching the wiki's conventions — unless
   the user asked for another language.
4. **Offer to file the answer.** A comparison, an analysis, or a connection you discovered
   is valuable and should not disappear into chat history. If the user wants it kept, write
   it to `wiki/queries/`, update `index.md`, append a `query` entry to `log.md`.

### Lint

Health-check the wiki. Look for:

- contradictions between pages;
- claims a newer source has superseded;
- orphan pages with no inbound links;
- important concepts mentioned but lacking their own page;
- missing cross-references;
- pages whose `source:` citation is missing, broken, or points at a file not in `raw/`;
- any page that contradicts `raw/knowledge.md` (**highest priority — report it**);
- data gaps that a web search could fill.

Then suggest new questions to investigate and new sources to look for. Append a `lint`
entry to `log.md`.

---

## 8. Boundaries

- **Never modify `raw/`.** See §2.1.
- **Never override `raw/knowledge.md`.** See §2.2.
- **Never ingest without being asked.** The wiki stays empty until the user says go.
- The LLM writes `wiki/` and `AGENTS.md` only. The human owns `raw/`, the game code, and
  the git history.
- Obsidian config lives in `.obsidian/` — leave it alone unless asked.
- Prefer editing existing pages over creating near-duplicates.
- Match existing conventions in the vault rather than inventing new formats.

---

## 9. Why this works here

The tedious part of a knowledge base is bookkeeping — updating cross-references, keeping
summaries current, noting contradictions, staying consistent across dozens of pages.
Stage 3 already shipped a six-phase tick pipeline, a determinism guarantee, and a test that
a 365-day unattended run is legal and reproducible. A wiki that is not maintained
stale will be worse than no wiki, and `raw/knowledge.md` is unusually precise about what
"correct" means for this project. Maintenance has to be near-free, and the rules have to
be honored literally, not approximately.
