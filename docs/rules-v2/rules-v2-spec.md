# Rulesets v2: Language Specification

Shape-locked 2026-07-14, provisionally. This document is the first of the three freeze
artifacts named by the authoring-UX plan (`the design notes in git history` §3.2): the
published expression EBNF, the reference namespace tree, and the `RuleHasher` resolved-identity
preimage field list. The shapes here appear in all three artifacts (this spec, the shipped
`rules/cs2demokit-rules.schema.json`, and the preimage-snapshot golden test), so the surface is
**locked as the working baseline**: no shape change without a recorded decision.

**Provisional, not discharged.** The plan's pre-freeze de-risk step (the live paper-test
with 2–3 non-programmers, §3.2) has **not yet run**; it remains the discharge condition that
turns this from provisional to final. The freeze is provisional precisely so that gate still has
teeth: if the paper-test surfaces an authoring gap, the shape can still change.

**Hash contract is intentionally breakable pre-1.0.0.** The application has not shipped a true
v1.0.0, so no author's rules depend on the resolved-identity hash yet. A post-freeze shape change
is therefore a **deliberate decision to re-baseline the preimage-snapshot golden**, not a
compatibility break; it is settled that the hash contract may break before 1.0.0
without migration concern. (The corruption risk the hasher guards against (hash-equal ⇒ node
sharing) is unaffected; that invariant holds regardless of freeze state.)

This is the working contract for `src/Analysis/DemoViewer.NET.Analysis.Rules` (the semantic core).

**Scope.** This spec covers the expression language that appears in v2 expression slots
(`when:`, `where:`, `compute:`, trigger `condition:` forms, bucket `key:` parts, capture
value selectors), the identifier namespaces those expressions resolve against, and the
structural-hash preimage that drives node dedup. The full document-level YAML schema
(`ruleset:` files) is specified by the generated `cs2demokit-rules.schema.json` (the v2
loader + compiler) and is out of scope here except where a shape is listed as reserved.

**Design charter (from the plan's §1.5):** an owned language with CEL's discipline:
a published EBNF, a typed checker against the Catalog's per-slot scope environments, no
Turing-completeness, a closed function set, exception-free evaluation, provable purity,
and statically enumerable read sets. The audience is non-programmers; every error message
names what was written, what was expected, and where (`file(line,col)`).

---

## 1. Lexical grammar

Input is a single-line or multi-line UTF-8 string (a YAML scalar). Tokens are separated by
optional whitespace (space, tab, newline). Comments are not part of the expression language
(YAML owns comments).

```ebnf
token          = identifier | number | duration | string | operator | punct ;

identifier     = ( letter | "_" ) , { letter | digit | "_" } ;
letter         = "A".."Z" | "a".."z" ;
digit          = "0".."9" ;

number         = int-literal | float-literal ;
int-literal    = digit , { digit } ;
float-literal  = digit , { digit } , "." , digit , { digit } ;

duration       = ( int-literal | float-literal ) , ( "s" | "ms" ) ;   (* no space *)

string         = '"' , { string-char | escape } , '"' ;
string-char    = ? any character except '"', '\', or newline ? ;
escape         = "\" , ( '"' | "\" | "n" | "t" ) ;

operator       = "==" | "!=" | ">=" | "<=" | ">" | "<"
               | "+" | "-" | "*" | "/" | "%"
               | "&&" | "||" | "!" ;                                  (* symbolic forms *)

punct          = "(" | ")" | "[" | "]" | "," | "." ;
```

**Word-form operators.** The identifiers `and`, `or`, `not`, `in` are reserved words and
lex as operators (`and` ≡ `&&`, `or` ≡ `||`, `not` ≡ `!`; `in` has no symbolic form). The
identifiers `true`, `false`, `null` are reserved literal keywords. Reserved words are
case-sensitive (only the lowercase forms are reserved); they may not be used as the first
segment of a reference.

**Hard EOF rule.** Every character of the input must be consumed by a token, and the parser
must consume every token. An unrecognized character is a lexical error naming the character
and its column; trailing tokens after a complete expression are a parse error. (This
replaces the v1 tokenizer's silent character-skip and silent truncation:
`ExpressionCompiler.cs` drops unknown characters and ignores trailing tokens today, which
lets a malformed condition silently evaluate as a shorter expression.)

**Duration literals** are lexed as a single token: `10s`, `0.5s`, `500ms`. The suffix must
be immediately adjacent to the number. Bare integers in tick-typed positions remain ticks.
The `"m:ss[.frac]"` timespan form is a **YAML slot scalar only** (e.g. `window: "1:30"`):
it is not an expression literal and does not appear in this grammar. It is **live in v2.0**:
a duration-typed slot (the streak `window:`, a `duration` param default) folds it to ticks
at the context tick rate exactly as an `s` literal (`"1:30"` = 90s = the same tick count as
`90s`).

---

## 2. Syntactic grammar

Standard precedence-climbing grammar. Lowest precedence first:

```ebnf
expression     = or-expr ;

or-expr        = and-expr , { ( "||" | "or" ) , and-expr } ;
and-expr       = not-expr , { ( "&&" | "and" ) , not-expr } ;
not-expr       = ( "!" | "not" ) , not-expr
               | comparison ;

comparison     = additive , [ comp-op , additive ]
               | additive , "in" , list-operand ;
comp-op        = "==" | "!=" | ">" | ">=" | "<" | "<=" ;

list-operand   = reference | list-literal ;
list-literal   = "[" , [ scalar-literal , { "," , scalar-literal } ] , "]" ;
scalar-literal = number | duration | string | "true" | "false"
               | "-" , number ;

additive       = multiplicative , { ( "+" | "-" ) , multiplicative } ;
multiplicative = unary , { ( "*" | "/" | "%" ) , unary } ;
unary          = "-" , unary
               | postfix ;

postfix        = primary , { member-access | index-access } ;
member-access  = "." , identifier ;
index-access   = "[" , expression , "]" ;

primary        = number | duration | string
               | "true" | "false" | "null"
               | function-call
               | identifier            (* head of a reference *)
               | "(" , expression , ")" ;

function-call  = function-name , "(" , [ expression , { "," , expression } ] , ")" ;
function-name  = "min" | "max" | "abs" | "contains" | "startswith" | "floor" ;
```

Notes:

- **References** are identifier heads extended by member access:
  `event.Attacker`, `victim.health`, `round.bomb.was_planted`, `myruleset.kills`.
  Resolution is the checker's job (§4); the parser only builds the dotted chain.
- **Comparisons do not chain**: `a < b < c` is a parse error (one optional comparison per
  level), matching CEL and avoiding the classic silent-truth trap.
- **Unary minus** is legal (`-99`, `-x`), a v1 parse error, fixed here.
- **`in`** takes a scalar on the left and either a list-typed reference (a `define:` list,
  a list-valued capture stat) or a constant list literal on the right.
- **Index access** `ref[expr]` serves both list element reads (int index, bounds-checked)
  and map lookups over `define:` maps (string key). Both type-check per §3.
- The **function set is closed**: exactly the six names above. Additions are a
  minor-version change to this spec and the schema together; removals never happen.
  (`floor` was added post-2.0 as such a minor-version addition.)
- **`not` binds looser than comparison** in this grammar: `not a > 1` parses as
  `not (a > 1)`: deliberate (the useful reading for non-programmers) but the opposite
  of most programming languages; the workbench's paren-hinting should surface it.
- **List literals and signs**: `scalar-literal` admits `-` on plain numbers only:
  `[-5]` is legal, `[-5s]` is not (write the tick int, or hoist to a define). Negative
  durations remain legal in ordinary expression position via unary minus (`-0.5s`).
- **Pseudo-members resolve last**: `.count` / `.set` are tried only after real member
  lookup fails, so a catalog symbol that genuinely exposes a `count` member wins over
  the pseudo-member.

---

## 3. Types, coercion, and evaluation semantics

### 3.1 Types

| Type | Values | Sources |
|---|---|---|
| `bool` | `true` / `false` | flags, comparisons, `.set` |
| `int` | 64-bit signed | counters, sums, slots, ticks, most event fields |
| `float` | IEEE double | quantized floats, `compute:` results |
| `string` | UTF-8 | names, weapon classes, bucket keys |
| `duration` | tick count (int at runtime) | duration literals, instant − instant |
| `instant` | tick position (int at runtime) | `event.tick`, `match.tick`, captures thereof |
| `list<T>` | immutable list of scalar `T` | `keep: list` captures, `define:` lists |
| `null` | the missing value | any missing/never-set read |

`duration` and `instant` are checker-level types (the durations-and-instants design in the
plan): at runtime both are int ticks. The checker enforces the algebra: `instant −
instant = duration`, `instant ± duration = instant`, `duration ± duration = duration`,
duration × / ÷ int scalar = duration; `instant + instant` is a type error. Bare int
coerces **one-way** into either time type where required by context; time types never
implicitly coerce back to bare int.

### 3.2 Coercions

- `int ↔ uint` wire values: unsigned wire fields (handles, uint32 counters) present as
  `int`; no separate unsigned type exists in the language.
- `int → float`: implicit when either operand of an arithmetic or comparison operator is
  `float`. There is no `float → int` implicit coercion; none of the closed functions
  truncates.
- `int → duration` / `int → instant`: implicit one-way where the context demands a time
  type (comparison with a duration, duration slot). Folding direction (§5) makes `5s` and
  `320` hash-identical at 64 ticks/s.
- **Disambiguation when both time coercions are demandable** (e.g. `instant − 320`): in
  *arithmetic* with a time-typed operand, a bare int coerces to **duration**, so
  `instant − int = instant`, `instant + int = instant`, and `int − instant` is a type
  error (there is no duration−instant form). In *comparisons*, a bare int coerces to the
  other operand's time type.
- **No other implicit coercions.** `string` never coerces to a number; `bool` never
  coerces to `int`. Both are type errors at check time.

### 3.3 Null semantics (FEEL-style)

The single most important runtime rule: **a 60k-tick replay never throws.**

- A read of a missing value (unseen entity field, unset capture element, absent event
  field, out-of-range list index) evaluates to `null`.
- `null` **propagates** through arithmetic, unary minus, and every closed function:
  any `null` operand → `null` result.
- Every **comparison** (`==`, `!=`, `>`, `>=`, `<`, `<=`, `in`, `contains`,
  `startswith`) with a `null` operand evaluates to `false`, with one carve-out: when
  either operand of `==`/`!=` is the **`null` literal**, the comparison is a *presence
  test*: `x == null` is `true` iff `x` is missing, `x != null` iff present, and the
  degenerate `null == null` is `true` (both operands are literals; the presence-test
  rule wins). The `null` literal is legal **only** as an operand of `==`/`!=`; using it
  in arithmetic, other comparisons, list literals, or function arguments is a check-time
  error.
- Logical operators treat `null` as `false` (`null and x` = `false`, `null or x` = `x`
  as bool).
- **Division or modulo by zero evaluates to `null`** (then propagates / compares false).
  Note this diverges from the v1 compiler's safe-divide-to-`0`; the corpus-port gate
  (golden accuracy suite) is the check that no shipped rule observed the difference.

### 3.4 List-typed values

List-typed stats (`keep: list` captures) are legal in expressions **only** via:

- `.count`: element count (`int`; the empty list is a real value, never `null`, so
  `count == 0` is the emptiness signal),
- `[n]`: bounds-checked element read (out of range → `null`),
- `.set`: see below.

Arithmetic or comparison on a whole list value is a type error (checker-enforced; no
list equality, no list arithmetic). `define:` lists additionally serve as the right
operand of `in`. A **map-valued `define:`** (a string-keyed lookup table
(`weapon_class: {ak47: rifle, awp: sniper}`) is read only through `ref[key]` subscript,
which yields the mapped value or `null` on a miss (mirroring list `[n]` out-of-range).
A map's values are uniform: all numbers or all strings (a mixed map is a structural
error). The whole map value never combines with an operator; `[key]` is its only read.
(Un-reserved by the vocabulary wave.)

### 3.5 The `.set` pseudo-member

`.set` never compiles to node `IsActive` (a `RoundScopedValueNode` is permanently active,
which would make the gate silently always-on, the exact bug class v2 exists to kill).
On a **scalar** capture, `.set` compiles to a hidden round-scoped bool twin activated by
the capture's own trigger; on a **list** capture it is sugar for `count > 0`.

### 3.6 Purity and read sets

Expressions are pure: no assignment, no side effects, no I/O, no nondeterminism, and
evaluation cannot throw. Every reference in a checked expression is statically resolvable,
so the exact **read set** of any expression is enumerable at compile time; this feeds
the lazy-scanner-activation reference enumeration and the engine track's `DeclaredReads`
read-aware edge ordering.

### 3.7 Closed functions

| Function | Signature | Semantics |
|---|---|---|
| `min` | `(int|float|duration, same) → same` | smaller operand; mixed int/float → float; mixed int/duration → duration (the §3.2 one-way rule); instants rejected |
| `max` | `(int|float|duration, same) → same` | larger operand; same mixing rules as `min` |
| `abs` | `(int|float|duration) → same` | absolute value; instants rejected (`-instant` is likewise a type error) |
| `floor` | `(int|float|duration) → same` | largest integral value ≤ the argument; instants rejected |
| `contains` | `(string, string) → bool` | substring test, ordinal, case-sensitive |
| `startswith` | `(string, string) → bool` | prefix test, ordinal, case-sensitive |

All six propagate `null` (§3.3). String comparisons are ordinal (no culture, no case
folding): bucket keys and weapon class names are wire identifiers, not prose.

---

## 4. The namespace tree

References resolve left-to-right against per-slot scope environments supplied by the
generated Catalog (`rules/catalog.json`). Which roots are in scope depends on the slot:
an event-triggered `where:` sees `event.*` and the role handles; a `when:` flag sees
stats and contexts but no `event.*`; a `compute:` is round-end and sees round-scoped
stats. The checker rejects out-of-scope roots by name with the slot's allowed roots in
the message.

### `compute:` cadence: round-end (default) vs live

A scalar `compute: "<expr>"` evaluates **once at round end** (a `ComputeOnRoundEndEdge`), the
default and byte-identical to the pre-live behavior. The mapping form `compute: { value: "<expr>", live: true }`
opts into **live cadence**: the compute re-evaluates *during* the round whenever any of its declared
reads change, so downstream reads and the per-message snapshot timeline observe the current value
rather than only the round-end one. Semantics of the live recompute:

- **Ordering (observes final inputs).** A live compute recomputes only in the eval loop's
  dirty-settle stage, **after** the per-message logic recompute queue drains to empty, so every
  rising-edge counter the message writes (rising-edge actions fire *inside* the logic drain) already
  holds its final value. A live compute reading a rising-edge counter therefore sees the post-write
  value, never a stale one.
- **Fixpoint.** If a live compute's value feeds a `when:` / `while:` logic node or another live
  compute, that dependent re-evaluates in the same settle (logic → live-compute → logic, iterated to
  fixpoint). Live computes fire in registration (document) order, so a compute reading another live
  compute observes the upstream's fresh value when the upstream is authored first.
- **Hard frequency cap.** A live compute recomputes **at most once per evaluated message** (a
  per-message once-fired latch keyed to the compute node, cleared each message). Multiple within-tick
  dirties of its reads dedup into a single recompute after the inputs settle; the value is memoized
  between recomputes, so downstream reads are O(1) and never trigger one. This is a hard bound, not a
  lint.
- **Round reset.** A round reset dirties the round-scoped counters a live compute reads, so the
  compute recomputes against the reset (zeroed) values on the reset message; it does not linger at
  the previous round's value.

Live cadence is **identity-bearing** (§6 row 8): a live and a non-live compute over the same formula
are not behaviorally interchangeable and must not share a node.

```text
event.<Field>                      # the triggering event's fields (catalog: events family)
                                   #   <Field> is the SDK payload record's property name,
                                   #   event.DmgHealth, event.WeaponItemId. Matching is
                                   #   case-insensitive; the catalog carries the canonical
                                   #   spelling and the wire's snake_case never resolves.
event.tick                        # instant. Every event carries its tick, an alias for the
                                   #   envelope's ServerTick. The loader rewrites the lowercase
                                   #   form; the compiler also resolves it (any case) off the
                                   #   envelope parameter, so it works in raw conditions
                                   #   (graph breakpoints) too. Wire fields shadow the alias.

kill / death / assist / …          # curated actor-anchored views (catalog: views)
  └── match: keys per view         # structured bindings (weapon:, headshot:, …)

victim.* / killer.* / assister.*   # role-handle entity reads under event views
                                   #   (victim.health, killer.active_weapon_class, …)
                                   #   lowered to per-slot pre-frame provider reads

player.<provider>                  # per-player entity providers (catalog: providers)
  player.health, player.armor, player.equipment_value,
  player.active_weapon_class, …
player.slot / player.team / player.name   # PlayerEnv bindings (frozen at materialization)

entity.game.<provider>             # singleton providers (entity.game.freeze_period, …)

enrich.<enrichment>.<output>       # enrichment outputs (catalog: enrichments family)

round.*                            # built-in round contexts (catalog: contexts)
  round.number, round.phase, round.bomb.was_planted (sticky)
  round.team.alive / round.team.players (int)         # team aggregates (subject's team)
  round.enemies.alive / round.enemies.players (int)   # team aggregates (opposing team)
  round.alive.in_clutch (bool)                        # clutch facet
  # (round.team.equipment / round.enemies.equipment: freeze-end economy sums)

match.*                            # match-level built-ins
  match.tick (instant), match.map, match.source_profile

net.<MessageName>                  # net-message trigger family (catalog: netMessages)
                                   #   trigger position only, never a value read; its payload
                                   #   fields read under event.<Field> in where:/match: (live)

<stat-id>                          # same-ruleset stats, bare
this                               # the enclosing stat's own current value (a non-stat Value
                                   #   symbol typed as that stat's value type; NOT a stat ref,
                                   #   so it never self-embeds a hash; preimage marker (ref this))
<ruleset>.<stat-id>                # cross-ruleset reads: QUALIFIED FORM ONLY,
                                   #   resolved against the exports: graph
<define-id> / <param-id>           # list/expr/map defines (inlined pre-hash) and params (bound
                                   #   to literals pre-hash); a map inlines as its (key-sorted) table
```

Resolution rules:

- **Bare stat ids** resolve within the enclosing ruleset only. Cross-ruleset reads are
  the qualified `ruleset.stat` form only; an unqualified match against another
  ruleset's stat is an error (this is what keeps resolved-identity hashing sound).
- Role handles (`victim.*` / `killer.*` / `assister.*`) exist only under event views
  that bind them; the member set after the role is exactly the per-player provider set.
- **`this`** resolves as a non-stat Value symbol whose type is the enclosing stat's value
  type; it is legal at value/`compute:`/`where:`/`when:` sites, is excluded from stat
  -reference cycle edges (it is not a stat reference), and a stat literally named `this`
  is a shadowing error.
- The team-aggregate namespaces (`round.team.*`, `round.enemies.*`, `round.alive.*`) are
  **live** (un-reserved by the vocabulary wave): subject-relative alive/connected counts and
  the clutch facet resolve, type, and lower to per-player nodes recomputed from the alive index
  (the single-writer design). Read them in `when:` / `while:` / `compute:`. The
  `net.*` trigger family's `match:`/`where:` payload matching is likewise **live**:
  a `net.<Message>` trigger exposes its payload fields under
  `event.<Field>`, typed from the `netMessages` catalog, in its `where:`/`match:` scope.
- Shadowing is an error, not a warning: a stat id that collides with a built-in context
  name, a define, or a param fails the build naming both definition sites.

### Entity-read timing: at-fire vs settle-time

Subject-player entity reads (`player.<provider>`, e.g. `player.health`) and the role handles
(`victim.*` / `killer.*` / `assister.*`) are valid in **five** expression sites, but they resolve at
**two different times** depending on the site:

- **At-fire sites** (`where:`, a `sum:`/`capture:` **value selector**, and `while:`) read the
  entity value at the triggering event's frame (the pre-frame provider read the role handle lowers to).
  These fold into the fire-time event condition / value selector (`ExpressionCompiler.CompileEventCondition`
  / `CompileEventValueSelector`).
- **Settle-time sites**, meaning `flag: when:` (round-end settle) and `compute:` (round-end, or live per the
  `compute:` cadence subsection above),
  have no event frame, so they read the subject's entity value at **settle time** (the round-end entity
  state, or the current state on a live compute). The read is materialized as a subject-relative
  `EntityValuePullNode` and gated through a multi-source edge (`when:`) or remapped into the node-
  expression compiler (`compute:`).

Both timings bind the value to the enclosing per-player subject slot (never a global). The distinction
is behavioral: an at-fire read observes the state *as the event fired*; a settle-time read observes the
state *as the round/message settled*.

### Scope: `for:` × `per:`, and match scope

A ruleset's `for:` selects the subject axis and `per:` the reset window; together they are preimage row 3.

- **`for: each_player`** materializes one per-player node template per stat; the view's implicit
  actor-role binding is **live** (a `count: kill` counts the subject's kills) and is carried by
  preimage row 10.
- **`for: match`** (game-scoped) has **no subject**: stats lower to single game-scoped graph nodes, not
  a per-player template. The view's actor binding is **suppressed** at edge-build time (a match-scoped
  `count: kill` counts *every* kill), and subject-relative reads (`player.*` and the
  `round.team.*` / `round.enemies.*` / `round.alive.*` namespaces) are **rejected** (no subject to
  bind them to). `show: scoreboard:` is inherently per-player and is **rejected** at match scope;
  `show: tables: per: match` projects a single match-level row via the `OutputScope.PerMatch` output
  scope (a one-row table, not a per-player grid).

---

## 5. Canonicalization and the normalizer

The canonical AST is the dedup and hashing unit. Normalization is deliberately
**conservative**: token-level and structural only, no algebraic rewriting:

1. **Whitespace and word forms** vanish at lexing (`a>1` ≡ `a > 1`; `and` ≡ `&&`).
2. **Parentheses** vanish at parsing (the AST is the precedence): `((a + b)) * c` ≡
   `(a + b) * c`. Parens that change grouping are, of course, different trees.
3. **Duration literals fold to int tick constants** using `ParsedDemo.TickRate` (the
   demo-less `rules check` path uses the parser's own 64/s default and states the
   assumption), `MidpointRounding.AwayFromZero`. Folding happens **before hashing**, so
   `5s` ≡ `320` at 64/s and they dedup together.
4. **Defines are inlined** at their use sites before hashing (a define shared across
   rulesets dedups by construction). Whole-node substitution: a reference-bodied define
   splices under member tails; an expression-bodied define used with a member tail is a
   check error; define cycles are a build error naming the cycle path. **Params bind to
   their concrete literal values before hashing** by the same inlining mechanism, so two
   installs of a blueprint with different param values produce different preimages.
   **Map-valued defines inline the same way** (§3.4): a `ref[key]` use expands to the
   map's constant table (serialized key-sorted, so author key order is not identity-bearing),
   and the whole subscript hashes as the `(index (map …) key)` node.
5. **`match:` bindings normalize** to their `where:`-equivalent comparisons in a fixed
   key order (the view's catalog key order), left-associative `and`. A **composed
   trigger condition** (a define-spliced trigger under a use site) canonicalizes to a
   single fixed conjunction order so the spliced and inline spellings of the same
   constraint hash identically:

   ```
   [merged match: bindings in catalog key order]  ∧  [view baked: filters in views.yaml order]
     ∧  [define where:]  ∧  [site where:]
   ```

   Merging the define's and the site's `match:` maps unions their keys into one catalog
   -key-ordered lowering; a **duplicate key across define+site is an error** (no silent
   last-wins). The implicit per-player **actor-binding equality is outside this hashed
   AST** (it is a planner-side edge/source check applied per slot at edge-build time); the
   view's actor **role** that selects that slot is carried by preimage row 10, so two stats
   whose views bind different actor slots (`kill` vs `assist`) hash apart, and `while:`-derived
   gates compose separately into preimage row 7.
6. **Literal sign folding**: unary minus applied directly to a numeric or duration
   literal folds into the literal (`-0.5s` ≡ `-32` at 64/s). This is spelling-level
   canonicalization, not arithmetic.
7. **No constant arithmetic folding, no operand reordering, no De Morgan rewrites** in
   v2.0 (`1 + 2` ≢ `3`). Hash-equal must mean behaviorally interchangeable under
   reference-identity node sharing; the normalizer earns trust by staying small.
   (Property gate: hash-equal ⇒ identical evaluation on the golden demos; the
   structural-dedup battery.)

---

## 6. `RuleHasher` resolved-identity preimage

The preimage for a v2 stat/rule node, in serialization order. **Any change to this list
after freeze is a breaking change** audited by the preimage-snapshot golden test
(freeze artifact 3).

| # | Field | Notes |
|---|---|---|
| 1 | `kind` | the `RuleHasher.KindName` token: `flag` / `count` / `sum` / `capture` / `bucket` / `compute` / `highlight` / **`tally`** / **`streak`** / **`rate`**. (Resolved pre-freeze: `KindName` now has an explicit `RuleNodeKind.Rate => "rate"` arm, so a `rate:` stat's row-1 token serializes as `rate` per design intent; it previously fell through to `none`. The `_ => "none"` fallthrough is now unreachable by any live kind; `RuleNodeKind.None` remains guarded from hashing.) |
| 2 | `value-type` | the §3.1 type, including `list<T>` element type |
| 3 | scope axis | the compound `(For × Per)` product: `match` / `round` / `player_match` / `player_round` (these four are the live `ScopeAxis` values; the extra bucket `per:` axes remain reserved (§7) and would add axes here if/when they ship). **A per-player `per:round` stat and its `per:match` twin MUST hash differently**: they share rows 1,2,4–8 and differ only here; a single-valued match/round axis would false-dedup them (corruption class) |
| 4 | resolved concrete-event set | the trigger's expansion to concrete event types, sorted; empty for untriggered stats |
| 5+6 | canonical AST **with embedded referenced-stat hashes** | one physical row: the §5-normalized AST serialized with every stat reference contributing the referenced node's own **resolved structural hash** in place of its name (recursive): identical text resolving to different nodes must hash differently (the false-sharing hazard caught in the round-2 review; text-keyed hashing is corruption under v2 scoped namespaces). Serialized form: `(stat <hex64> [tail…])`, name absent. **Multi-AST kinds** (`sum:`/`capture:` carry both a trigger condition and a value selector; a summing `bucket:` (one with a `value:`) carries its summed amount in the same value slot): the trigger condition is row 5's expression and the value selector is an appended slot, **each length-framed**, `(cond <len>:<cond> | value <len>:<value>)`, so a capture and a sum with the same trigger but different value selectors do **not** dedup, a summing bucket and a count bucket over the same trigger + key do **not** dedup, and a `|`/space inside a user string literal cannot forge a slot boundary (the length prefix is normative, matching the row-level wire framing below). The `this` self-reference (§4) serializes as the fixed marker `(ref this)`, never a row-6 hash embedding (a node cannot embed its own not-yet-computed hash). **The implicit per-player actor-binding equality is NOT part of this AST**: the view's actor role is carried separately by row 10 (see below), and the per-slot equality itself is applied by the planner at edge-build time, never baked into this trigger AST |
| 7 | gate id | the resolved hash of the `while:` gate node, if gated (distinct from the row-5 trigger condition) |
| 8 | keep-spec | first / last / list / **min / max** (a scalar `capture:` keeping the running minimum / maximum of a **numeric** value over its `per:` window; a non-numeric value is a check error; an **unseen** window takes the first value verbatim, never min/max against the value node's phantom 0, mirroring the bucket min/max reducer; `keep: max` and `keep: last` over the same value hash apart because this row participates in identity); **tally thresholds as `(min, target)` pairs**: the `target` (the emit-node id each threshold writes to) is behaviorally load-bearing and part of identity (v1's `RuleHasher` hashes both; two tallies with the same mins but different emit targets write to different counters and must NOT dedup), serialized sorted by `(min, target)`. A threshold's `min:` is **live as either an int literal or a `params.<name>` reference** to a bound `int` param (an undeclared, non-`int`, or malformed reference is a check error): the param binds to its literal int value **before** this pair is built, exactly like every other param inline (§5 point 4), so `min: params.x` with `x = 3` folds to `(3, target)` and dedups with a literal `min: 3` (identity is over the folded int, never the reference text). **streak window + min-streak**; **bucket key-part list + reducer**: the key-part list is **ordered** (a composite/tuple key `[a, b]` and `[b, a]` select different tuples and MUST NOT dedup) and joined on a Unit Separator (`U+001F`) no rendered key-expression text contains, so a single part hashes byte-identically to the pre-lift single key. The reducer name (`sum`/`min`/`max`/`last`/`first`, or **absent** for a `count`, normalized to null so an implicit count and an explicit `reduce: count` dedup) is identity-bearing here, and a value-reducing bucket's `value:` selector additionally rides row 5's value slot (see §6 row 5+6), so two buckets with the same key + trigger but a different `value:` expression, or a different `reduce:`, hash apart (a count bucket and a sum bucket never dedup; two sum buckets summing different amounts never dedup; a max bucket and a sum bucket over the same key + value never dedup). **compute `live:` cadence**: a `;live` marker appended **only** when a `compute:` opted into live cadence (`compute: { value, live: true }`), so a live and a non-live compute over the same formula hash apart while a non-live compute (and every v1 caller) keeps byte-identical row-8 bytes. A compute's `format:` (display precision, §7) is **not** carried here: it is presentation-only, outside identity, so two computes differing only in `format:` dedup (first-wins, like the display `label:`) |
| 9 | id-salt for input-less stats | applies iff the node has **no concrete events, no trigger expression, no value selector, and no gate**: such a stat hashes its own id so two empty counters stay distinct. A value selector counts as an input just like the trigger condition (it is a row-5 slot), so two structurally identical value-only captures are **not** id-salted and dedup normally. Any input present ⇒ the id leaves the preimage entirely (same-shaped nodes dedup across ids) |
| 10 | view actor-role binding | the view's implicit per-player actor role, as a canonical token: the view name (`kill` / `assist` / `death` / …) when the view binds an actor, the fixed token `suppressed` when `match: { actor: any }` turned the binding off (identity then comes from the explicit `where:` already in row 5), and **absent** for nodes with no view (raw / net / expression / compute). **Emitted only when present**, so every node without a view (and every v1 caller, which never sets it) hashes byte-identically to the pre-row-10 preimage. This row keeps a `count: kill` (actor = killer) and a `count: assist` (actor = assister) apart: they share rows 1–9 (same kind, type, scope, concrete events, baked `event.Attacker != event.UserId` trigger) yet write different per-player values, and the slot equality that distinguishes them is applied by the planner at edge-build time, not baked into the row-5 AST. Same-view stats carry the same token and still dedup (cross-name sharing is preserved) |

**Wire framing (normative):** the rule preimage is the UTF-8 string
`dvr2|<row#>:<byte-len>:<bytes>|…` over the rows above in order; the standalone
expression preimage uses prefix `dv2-expr|`. The hash is SHA-256; the human-readable
form is lowercase hex.

Cycle rule: stat-reference cycles are a build error (named cycle path), so the recursive
embedding at row 6 terminates. Params are bound before hashing (two instantiations of a
blueprint with different param values hash differently, as they must; they are different
nodes).

---

## 7. Reserved / freeze-relevant shapes (schema freeze list)

This section is the freeze record of the shapes the shipped `cs2demokit-rules.schema.json` must carry
(freeze artifact 2). Nearly all of them are **now live in v2.0**: the vocabulary/composition
wave un-reserved them, and each is called out inline below. The **only** shape still reserved
(present in the schema but loader-rejected as "not yet implemented") is the **extra bucket
`per:` axes** (the per-key grouping axes, under the rate kind's match-scoped bucket).
Everything else here documents a live shape and its freeze-relevant semantics:

- Multi-source conditional edges: condition source **lists**: **live in v2.0**.
  A `flag:`/`highlight:` `when:` may be authored as a YAML **list** of predicate
  strings; the list is the **AND-conjunction** of its items (`when: [a, b]` ≡ `when: "(a) and
  (b)"`). It is pure authoring sugar collapsed at the model boundary: each item is parenthesized
  and joined with ` and ` into the same string the scalar `when:` path parses, so the resolved AST,
  the identity hash, and the planner lowering (into the multi-source conditional edges the engine
  already builds) are byte-identical to the joined-string form. A single-item list `when: [p]` ≡ the
  scalar `when: "p"`; an empty list `when: []` is a structural error (a `when:` must constrain
  something).
- Bucket lifts: **live in v2.0**: a `value:` numeric event-scope expression reduced per
  key (weapon-stats' `damage_by_weapon` ports onto the SUM reducer); `key:` as a **list** (an
  ordered composite/tuple key); and the named **reducers** `reduce: sum | count | min | max |
  last | first` (default `sum` when a `value:` is present, else `count`, so every pre-lift bucket
  is unchanged). Still reserved: the extra bucket `per:` axes.
- Per-key rate: **live in v2.0**: a `rate: { of: <bucket>, per: <bucket> }` stat, a
  **derived** per-key ratio of two same-keyed sibling `bucket:` stats (`of` = numerator, `per` =
  denominator), e.g. per-weapon headshot %. **Semantics (locked):** the output iterates the
  **denominator (`per`) key set**: a rate is defined only where the population base exists (a
  union would invent phantom 0-denominator keys; an intersection would drop legitimate
  0%-numerator rows like `knife` = kills-but-no-headshots). A numerator-**missing** key ⇒
  numerator 0 ⇒ ratio `0.0` (a real row); a denominator key present but `== 0` ⇒ ratio undefined
  ⇒ the key is **skipped** (no row: a count bucket can't be 0, but a `sum`/`last`/`min` bucket
  can, so the guard is real). Both `of`/`per` are **required**, must be **numeric** buckets with
  **identical** `key:` parts (else the two key spaces aren't comparable → attributed error), and
  the rate is **match-scoped** like a bucket (`per: match`; `per: round` is rejected). **Identity:**
  the resolver synthesizes an `of / per` division into the row-5 expression slot, so two rates over
  different bucket pairs hash apart via the row-6 embedded referenced-node hashes; no new preimage
  row (byte-identical preimage shape for every non-rate node). The `KeyedRatioNode` is
  snapshot-excluded and projected per-key as a table of **floats** (a 0.5 rate stays 0.5, never
  coerced to int like a whole-number count bucket).
- Durations: all three literal forms (`10s` / `0.5s` / `500ms`) and the
  `"m:ss[.frac]"` YAML slot scalar are **live in v2.0** (durations fold at the context tick
  rate). `show: as: ticks|seconds|time` column formatting is likewise **live**: on a
  scoreboard or table column over a tick-valued stat, `ticks` projects the raw integer tick
  value, `seconds` projects ticks / tick-rate (a double), and `time` projects an `m:ss`
  string at the demo's tick rate. A column with no `as:` is unchanged (raw value).
- Compute `format:` (per-stat display precision): **live in v2.0**: a `compute:` stat may carry an
  optional `format:` scalar, a .NET numeric format string (e.g. `F2`, `F0`) the projector applies when
  rendering the computed value's display string (default `F1` when unset). It is a **presentation-only**
  attribute, **excluded from the resolved-identity preimage** (§6 row 8), exactly like the display
  `label:`, so two computes differing only in `format:` are behaviorally interchangeable and dedup
  (first-wins). It restores v1's per-`expression` `format:` precision (v2 previously hard-coded `F1`).
- Team aggregates: the `round.team.*` / `round.enemies.*` / `round.alive.*`
  namespaces. **Live in v2.0**: `round.team.alive` / `round.team.players` /
  `round.enemies.alive` / `round.enemies.players` (int, disconnect-aware subject-relative
  counts), `round.alive.in_clutch` (bool), and the freeze-end economy sums
  `round.team.equipment` / `round.enemies.equipment` (int).
- Role handles: `victim.*` / `killer.*` / `assister.*` members. **Live in v2.0** (not
  loader-rejected), listed here only because they are freeze-relevant shapes.
- Net-message seams: bare `net.<MessageName>` triggers are **live**, and their
  `match:`/`where:` **payload matching** is now **live** too. A
  `net.<Message>` trigger's `where:` reads its payload fields under `event.<Field>` (typed
  from the `netMessages` catalog family, same spelling a game-event view uses); an unknown
  field or a wrong-type comparison is an attributed error. `match:` on a net trigger is a
  field-facet form: each `{ Field: <test> }` lowers to an `event.<Field>` where:-conjunct,
  so a structured `match:` and the equivalent free-form `where:` hash identically. The
  planner lowers a conditioned net trigger through the v1 `CreateNetMessageEdge` path (the
  same `CompileEventCondition` a v1 net rule uses); a bare net trigger is value-identical to
  the v1 net path.
- Composition: map-valued `define:` (`ref[key]` lookup over a `define:` map) is
  **live in v2.0**: a string-keyed table of uniform values
  (all numbers or all strings), read as value | null.
- Provenance: `catalog_version`, `min_app_version` document fields are **live in v2.0**:
  free-form provenance strings the loader accepts and stores as human/tooling metadata (never
  a build input; no version-comparison validation is performed).

---

## 8. Error message contract

Every diagnostic from the semantic core carries `file(line,col)` (YAML mapping position of
the expression scalar plus in-expression offset), the offending source text, and, for
resolution errors, a did-you-mean candidate list from the Catalog: case-insensitive
Levenshtein distance ≤ 2, ranked by distance then ordinal, capped at 3 candidates. (This
is deliberately stricter than the v1 `rules check` convention of `max(2, len/3)`; the
CLI aligns to this spec when it adopts the semantic core alongside the v2 loader +
compiler.) Checker errors state the expected and actual types in language terms
(`duration`, `instant`), never CLR type names.
