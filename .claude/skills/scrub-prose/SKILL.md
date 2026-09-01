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

This file governs inside this repo. Where it disagrees with `~/.claude/skills/repo-scrub/references/human-voice.md`,
this file wins here and that one wins everywhere else. They currently disagree about the em-dash, on
purpose; see §3.

---

## Rule zero

**Every list below is a place to look, never a thing to run.** No rule here authorises `sed`, `perl
-pi`, or any scripted find-and-replace. A replacement is decided by a reader with the surrounding
sentence in view, one at a time.

The words on these lists are ordinary English and ordinary code. A blind pass renames identifiers,
corrupts string literals that tests assert on, breaks JSON keys and rewrites CLI help text. This
repo has already proved it: `"—"` is the live placeholder `GameInfo`, `FollowablePlayer` and
`DemoLibraryModels` return for "no value", and a global replace would have changed what the app
displays and broken the test pinning it.

Second half: **prose files get prose treatment, code files get comment treatment.** Nothing here
authorises an edit to an expression, identifier, literal, attribute or import. A comment token is
the only thing this skill may touch in a source file.

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

Three things are not prose and keep their character:

- An em-dash inside a code span or a `<c>` tag, where the glyph is the subject. `design-system.md`
  documents `—` as a character.
- A lone `—` in a table cell meaning "not applicable". Replace with `n/a` only if the column reads
  better for it.
- **The em-dash as a value.** `"—"` is this app's placeholder for "no value": `GameInfo.BombState`,
  `FollowablePlayer`, `DemoLibraryModels`. Where a doc lists it among a field's legal values
  (`"Warmup" | "Freeze" | "Live" | "—"`), it is data, not punctuation.
- **A doc quoting a user-facing string.** Product strings are neither comments nor documents and are
  out of scope, so `AnnotationSessionController` still returns `"session only — this browser tab
  forgets annotations when it reloads"`, and `wasm-matrix.md` quotes it verbatim. Editing the quote
  makes the doc wrong about the app, and one of these is pinned by
  `Playback2DAnnotationPersistenceTests`. Check the string exists in source before touching a quote.

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

## 9. Rhythm

The largest single lever, and the one this file missed for its first three revisions. Everything
above targets what a block *says*; this targets how it *moves*.

17. **Uniform sentence length.** Agent prose runs 15–25 words a sentence and almost never leaves that
    band. Human technical writing swings from 4 words to 40. Measure a block: if every sentence lands
    within ten words of the last, the block reads generated no matter how good the content is. Fix by
    splitting one long sentence and letting the next run on. A four-word sentence carries weight
    precisely because it is short.
18. **Even attention.** Real comments are lopsided. The thing that fought back gets three sentences;
    the thing that worked gets a clause. If every member in a file carries a similarly-sized block,
    the file is describing itself rather than warning anyone.
19. **Stacked hedging.** "may potentially", "could possibly", "it is generally the case that". State
    it, or state what would settle it. `<remarks>` is for a real caveat, not for softening a claim
    nobody disputed.

---

## 10. Structure

Shape-level tells that survive every word-level pass. Three of these are invisible after §3 runs,
because §3 changes their punctuation and leaves their shape intact.

20. **Manufactured antithesis.** `It is not X, it is Y` and `not merely X but Y`. Rule 5 already cuts
    "not merely"; this is the wider construction, and stripping the em-dash out of
    `not X — it is Y` produces `not X, it is Y`, which is the same tell wearing a comma. Say what it
    is. The contrast is almost never load-bearing.
21. **The rule of three.** Three parallel clauses, three bullets, three examples, over and over. Real
    content rarely divides into threes that neatly. Where a third item exists only to complete the
    cadence, cut it; where a fourth was dropped to preserve it, put it back.
22. **Uniform bullet runs.** A long list of `- **Term** — explanation`, every entry the same shape.
    §3 turns these into `- **Term**: explanation`, which does not help: the uniformity was the tell,
    not the dash. Convert some to prose, let some be three words and others two sentences, and drop
    the bold lead where it is decorating rather than naming.
23. **Two-column tables of term and description.** A table earns its place at three or more columns
    of genuinely tabular data. Two columns of noun-and-sentence is prose that has been formatted.
24. **Status glyphs as a system.** ✅ / ❌ / ⚠️ / 🚧 in tables and checklists. Replace with words, or
    with nothing. A genuine checklist uses `- [x]`.
25. **Typographic drift.** Curly quotes and the `…` character in a repo that is otherwise ASCII, and
    Title Case On Headings where the rest of the file uses sentence case. Match the file, not the
    convention you would pick.

---

## 11. Before you commit

26. **Did the rewrite change what the text asserts?** Ask it literally, of every hunk: was a fact,
    number, date, path, identifier, version, citation or claim added or lost? A scrub that improves
    the reading and alters the meaning has failed, and it fails silently. This is the one check worth
    doing twice.
27. **Grep a heading before renaming it.** Markdown anchors derive from the heading text, and
    punctuation is dropped rather than replaced, so swapping one mark for another moves the anchor.
    `#### Encoder ladder — \`--encoder\` / \`--quality\`` in `dv2d.md` answers to
    `#encoder-ladder---encoder---quality`: the em-dash contributes nothing but the space beside it
    contributes a hyphen, so replacing it with a colon silently drops one and breaks the link on
    line 252 of that same file. Resolve every `](#fragment)` against its target before and after.

    Where a heading is a link target and likely to be reworded, do what `design-system.md` does and
    give it an explicit `<a id="…"></a>`. Thirty-one of them, and none can be broken by an edit to
    the heading text.
28. **Commit in coherent pieces.** One commit touching every document in the repo is a
    mechanical-sweep signature, and it outlives every other thing the scrub cleaned up. Batch by area
    or by file group, say in the message what was deliberately left alone, and let the batches be
    different sizes.

---

## 12. Method

1. Get the diff range. Measure comment density per file; compare against an untouched neighbour in
   the same directory. **Target the house rate for that area (20–28 %), not zero.**
2. Rank files by `comment% × lines changed`.
3. Apply §3 first. The clause-level cuts are mechanical and need no judgement; the em-dash rewrites
   at the top of it are the opposite, so read every one and give the sentence real punctuation.
4. Then §5 (relocate): moving history to commit messages usually removes the longest blocks.
5. Then §4 (compress).
6. Then §10 (structure). Do it after the cuts, not before: half of what looks like a uniform bullet
   run turns out to be two runs once the dead clauses are gone.
7. Then §9 (rhythm), last of the editing passes, because it is the only one that reads the result as
   a whole rather than hunting a pattern.
8. Re-measure. Report before/after density per file.
9. Run §11. The assertion check is not optional and is not the same as reading the diff again.
10. **Build and run the tests.** Doc comments carry `<see cref="…"/>`; a bad edit breaks the build,
    and this repo treats warnings as errors.

Do not touch code behaviour in a scrub pass. If a comment is wrong, that is a separate fix; say so
rather than quietly correcting it.

A pass that changes what a comment claims has failed, however well it now reads.
