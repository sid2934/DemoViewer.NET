---
name: scrub-prose
description: Strip machine-authored tics from comments, doc comments, plan docs and commit messages so they read as this repo's own voice. Use after any large agent-written change, before review or merge. Targets block SHAPE and duplication, not vocabulary.
---

# Scrub prose

Agent-written code in this repo does not read wrong because of its words. It reads wrong because
its comments **argue**, **narrate their own history**, and **end on a moral**. The vocabulary is
mostly fine. Fix the shape.

Every rule below was derived by measuring one large agent-written range against the pre-existing
tree (`b658240`, 171k lines, 20 % comment). Rates are per 1,000 comment lines.

---

## 1. Leave these alone: they are the house voice

Measured at **1.0–1.1× the baseline rate**. Stripping them makes the code less like itself, not more.

| thing | baseline | verdict |
|---|---|---|
| ALL-CAPS emphasis (`ONE`, `NOT`, `BEFORE`) | 105 /1k | keep, in comments and commit headers |
| "deliberately" / "deliberate" | 6.2 /1k | keep: it is this repo's word for "we considered the alternative" |
| narrative commit subjects (`fix(X): the thing that was wrong, and what it exposed`) | 30+ commits | keep |
| `<b>` on an invariant that opens a `<para>` | structural | keep |

**Never delete a comment recording a tool, platform or framework fact** someone would otherwise
rediscover the hard way. These are why the house style is dense and they are the most valuable lines
in the repo. Examples that must survive any scrub:

- `dotnet test --filter` is silently ignored by this runner; `[A&B]` crashes the filter parser
- FluentTheme does not ship the `ColorPicker` control theme
- a TUnit TestId contains `:`, which is illegal in an NTFS path component
- a Fluent `NumericUpDown`'s chrome does not scale with the width it is given
- a VM in a bare `ContentControl` must derive from `ViewModelBase` or it renders as its `ToString()`
- at 30 fps `ticksPerOutputFrame ≈ 2.13`, so ticks are skipped

If unsure whether a fact is load-bearing, keep it and shorten it.

---

## 2. The house comment shape

> One sentence saying what it is. Optionally **one bolded invariant**. One mechanism or "otherwise"
> consequence. Stop.

```csharp
/// <b>The gesture stays bound to the pane it began on.</b> A drag that starts on the upper band and
/// wanders into the lower one keeps panning the upper band. Otherwise a fast drag across a band
/// boundary yanks two floors at once.
```

Limits observed in untouched files: **≤2 `<para>`** (typical 1), **longest block 15 lines** (typical
6–10), **zero** narrated history, **zero** aphorisms, **zero** plan-doc citations.

---

## 3. Delete

**The em-dash is banned outright.** It was on the keep list until 2026-08-31, on the measurement:
121 /1k, dead level with the pre-existing tree, so it was the house voice by the numbers. The owner
overrode that. The rate is still true and is no longer the deciding fact, so do not re-derive the old
verdict from it.

Removing one is a rewrite, never a substitution. Read what the dash was doing and give the sentence
the punctuation it actually needs:

- introducing an explanation or a list, use a colon
- setting off an aside, use commas, or lift the aside into its own sentence
- joining two clauses that can stand alone, use a full stop

`- ` is not the answer. It reads as a dash that lost its nerve, and it leaves the interrupter
stacking of rule 8 exactly where it was.

Two things are not prose and keep their character: an em-dash inside a code span or a `<c>` tag
(`design-system.md` documents `—` as a glyph), and a lone `—` in a table cell meaning "not
applicable". Replace the latter with `n/a` only if the column reads better for it.

Then the clause-level cuts. Search, then remove the clause. Each is ≥3× the house rate.

1. **Closing aphorism.** `/, which is how [a-z][^.]*\.$/` and `/\bis how (a|an|the|it|nobody) [a-z][^.]*\.$/`
   at end of sentence. Delete from the comma. These are morals, not facts.
   *"…to sixteen times the 13 pixels it needs, which is how a budget stops being a gate."*
2. **Meta-comment about the comment.** `/this (comment|paragraph|doc|property) (used to|claimed|promised|read)/`,
   `/That is the honest state of this/`.
3. **`<b>Why …</b>` / `<b>Why not …</b>` paragraph headers.** A comment does not need a rubric
   announcing that it is about to justify itself. Delete the header, keep the sentence.
4. **`and that is (the point|why|what|the payoff)`**.
5. **`not merely` / `not simply`** → plain "not", or delete the negated half.
6. **`which is precisely`** → delete "precisely", usually the clause.
7. **Identical flourishes across sibling files.** Grep any distinctive phrase before writing it. One
   range had `<b>Empty, and that is the point.</b>` verbatim in three files. No human writes the
   same eight-word flourish three times.

---

## 4. Compress

8. **Stacked interrupters**, whatever punctuation carries them. §3 removes the dashes; this is the
   habit underneath, and a sentence that reaches its point through two asides still does after they
   turn into commas (1.9× baseline). Cut one, or split the sentence.
9. **Trailing `, which is (the|what|why|how) …`**: at most one per `<summary>`; the rest become a
   full stop and a plain sentence. (4.0× baseline, 100 hits in one range.)
10. **`<para>` count > 2** → cut to 2.
11. **A `///` block ≥10 lines above a single-line member** (`=> …;`, `{ get; set; }`) → summary ≤2
    lines, mechanism to `<remarks>`, history to the commit.
12. **Any comment block > 20 lines** → the content belongs in a plan doc with a `see` reference.
13. **`<i>` for stress** → plain text. Fine for a quoted string.

`<summary>` says what it is. `<remarks>` says how it works and what will bite you. The commit says
what changed and why.

---

## 5. Relocate

True and useful, wrong place.

14. **`used to …` / `before D<n>` / `since D<n>`.** Ask: *does the reader need the old state to use
    this code correctly today?*
    - **Keep** for a live migration or compatibility obligation (a renamed settings key, a v1 schema).
    - **Move to the commit message** when it narrates a fix.
15. **Plan-doc citations in source** (`D6 finding 3`, `round 3A`, `the audit`). Measured at **419×**
    baseline; the pre-existing tree has essentially none. Replace with the *invariant* the code
    enforces. Cite a plan once per class, never per member.
16. **Derivation tables and measurements inside a `<summary>`** → the plan doc. Leave the resulting
    constant and one line saying where it came from.

---

## 6. Word-level

| find | rate | replace with |
|---|---|---|
| `honest` / `honestly` / `the honest state` | 6.8× | the fact: "the key is absent" |
| `nobody saw / nobody runs / nobody notices` | 14.7× | the mechanism: "no CI lane selected this category" |
| `worth having / worth naming / worth pinning` | 2.4× | delete the judgement, keep the fact |
| `stops being a gate / earns its place` | 5.2× | usually the tail of rule 1; delete |
| first person (`my`, `I`, "out of my file list") | n/a | always a fossil of the authoring agent; delete |

---

## 7. Duplication

Before adding a `<para>` that explains *why*, grep its distinctive noun phrase. If it exists
elsewhere, write `see <see cref="X"/>`.

One range shipped the same rationale **six times** (a settings key's absence), the same proof
obligation three times in near-identical paragraphs, and one aphorism four times across two docs and
a commit. Duplicated rationale is the strongest single signal of machine authorship, and the easiest
to detect: search the phrase, not the idea.

---

## 8. Commit messages

Keep: measurement tables, before/after counts, "verified with the runner CI actually uses",
`KNOWN NOT FIXED` sections naming the file:line that will fail next. A bisector needs all of it.

Cut: aphorisms and inverted restatements. `"The list is not the point. The point is why the tests
saw none of it."` is the purest instance of the genre.

Subject lines and ALL-CAPS section headers are house style. Leave them.

---

## 9. Method

1. Get the diff range. Measure comment density per file; compare against an untouched neighbour in
   the same directory. **Target the house rate for that area (20–28 %), not zero.**
2. Rank files by `comment% × lines changed`.
3. Apply §3 first. The clause-level cuts are mechanical and need no judgement; the em-dash rewrites
   at the top of it are the opposite, so read every one and give the sentence real punctuation.
4. Then §5 (relocate): moving history to commit messages usually removes the longest blocks.
5. Then §4 (compress).
6. Re-measure. Report before/after density per file.
7. **Build and run the tests.** Doc comments carry `<see cref="…"/>`; a bad edit breaks the build,
   and this repo treats warnings as errors.

Do not touch code behaviour in a scrub pass. If a comment is wrong, that is a separate fix; say so
rather than quietly correcting it.
