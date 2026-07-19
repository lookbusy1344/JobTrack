# JobTrack web design language — "Console"

This document describes the visual design language of the JobTrack ASP.NET Core front end. It is
descriptive, not normative: the authoritative behaviour spec remains `jobtrack_spec_codex.md`, and
this file explains *how* the browser UI looks and *why*, so future changes stay coherent.

The entire system lives in two places:

- `src/JobTrack.Web/wwwroot/css/site.css` — the design tokens and every component style, layered
  over the pinned Bootstrap 5.3 CSS-custom-property theming (`--bs-*`). Bootstrap's vendored files
  under `wwwroot/lib/bootstrap` are never edited or forked (see `CLAUDE.md`).
- `src/JobTrack.Web/Pages/**` — Razor markup composed from the small primitive vocabulary below.

## Concept

JobTrack is an operations console for tracked labour and dynamically-calculated cost. The design
reads as a **modern, faintly futuristic control surface**: a light working canvas of layered white
cards over a warm neutral paper ground, under a deep espresso command header, driven by a single
burnt-orange accent. Data is the hero — money and identifiers render in tabular monospace, and the
cost figure is treated as an instrument read-out rather than body text.

The burnt-orange-over-warm-neutral palette is a deliberate identity choice: it reads as a workshop
/ foundry costing tool and steps away from the generic indigo/blue SaaS default. The look is also
*not* a flat broadsheet ledger (hairline rules, uppercase everything, monospace body). Boldness is
spent in one place — the command header and the metric read-out — and everything around them stays
quiet.

## Tokens

All tokens are CSS custom properties on `:root` in `site.css`. Do not hard-code these values in
markup or scoped CSS; reference the token.

### Colour

| Role | Token | Value |
|---|---|---|
| Ink (body text) | `--jt-ink` | `#241c14` |
| Ink soft (labels) | `--jt-ink-soft` | `#3d3226` |
| Muted text | `--jt-muted` | `#6a5c4b` |
| Faint text / meta | `--jt-faint` | `#7c6c57` |
| Primary accent | `--jt-orange-600` | `#cf4409` |
| Primary hover/active | `--jt-orange-700` | `#b23808` |
| Bright fill (on dark / behind white text) | `--jt-orange-500` | `#f9640f` |
| Secondary glow accent | `--jt-gold-400` | `#fbbf24` |
| Canvas (page bg) | `--jt-canvas` | `#f1ece5` |
| Surface (cards) | `--jt-surface` | `#ffffff` |
| Hairline | `--jt-line` | `#e7ded2` |
| Command header | `--jt-console-900/800` | `#1a1109` / `#241812` |

The accent lives on a named ramp (`--jt-orange-50 … -700`): `-600` is the text/link/fill workhorse,
`-500` is the brighter fill used only behind white text or on the dark console, `-700` is
hover/active, and the tints (`-50`/`-100`/`-200`) back chips, hovers, and soft fills. The gold
(`--jt-gold-400`) is a secondary accent only — never load-bearing text on light surfaces.

Status colours use a paired background/foreground for AA contrast: green (`--jt-green-700` on
`--jt-green-100`), amber (`--jt-amber-700` on `--jt-amber-100`), red (`--jt-red-700` on
`--jt-red-100`).

**Accessibility is a hard constraint, not a preference.** Every page is scanned by axe-core in the
end-to-end suite (`tests/JobTrack.Web.EndToEndTests`, `RunAxe()`), which fails the build on any
critical/serious violation — including colour contrast below WCAG AA (4.5:1). The chosen values are
picked to clear that bar: `--jt-orange-600` is pushed as bright as AA allows for pop — ≈4.7:1 as
text/links on **white**, so any orange text must sit on a white surface (this is why `fieldset` is
white: its bright-orange `<legend>` dips to ≈4.35:1 on the off-white `--jt-surface-2`). `--jt-muted`
on white is ≈6.4:1 and `--jt-faint` (the smallest 11px labels) ≈5.1:1. Brighter fills that carry
white text (the primary button) stay at `--jt-orange-600`; `--jt-orange-500`/`-400` are used only
for glows, gradients, and the dark console, never as text on light. When adjusting any colour,
re-check contrast before committing.

### Typography

System fonts only — no webfonts, to keep the front end self-contained and fast (consistent with
the pinned-assets discipline in `CLAUDE.md`).

- **Display** (`--jt-font-display`): a rounded system stack (`ui-rounded` / SF Pro Rounded /
  Quicksand → Segoe UI / system-ui). Titles (`h1`/`h2`), the brand wordmark, and metric labels.
  Gives headings a younger, more characterful voice where the platform offers a rounded face, and
  degrades gracefully to the body sans elsewhere — no webfont dependency.
- **Sans** (`--bs-font-sans-serif`): Segoe UI / system-ui stack. Body, controls, labels.
- **Mono** (`--bs-font-monospace`): SF Mono / JetBrains Mono / Consolas stack. Identifiers, money,
  metric read-outs, and eyebrows — anything that benefits from tabular alignment or a "machine"
  register. The brand emblem itself is a spanner glyph (inline SVG) on a bright-orange chip.

Scale: `h1` 2.15rem / 800 weight / tight negative tracking; `h2` 1.45rem / 750; `h3`/`h4` are
**not** full headings but small uppercase console section-labels with a short orange→gold accent
tick. Body is 0.9375rem at 1.6 line-height. The root font-size steps from 14px to 16px at the 768px
breakpoint.

### Shape, elevation, motion

- Radius: cards `--jt-radius-lg` (1.125rem), controls `--bs-border-radius-sm` (0.5rem).
- Shadows are soft, layered, warm-tinted (`--jt-shadow-sm/md/lg`, plus `--jt-glow` for accents).
- Motion is minimal and subject-serving: a 6px page-load rise on `main`, hover lift on the primary
  button, and focus glow rings. All motion collapses to `0ms` under
  `prefers-reduced-motion: reduce`.

## Layout primitives

The markup composes these classes; styling lives entirely in `site.css`.

| Primitive | Purpose |
|---|---|
| `.app-header` | The deep, glowing command header. Sticky, with brand emblem, active-nav underlight, and a user status chip. |
| `.jt-page-head` + `.jt-eyebrow` | Page title block: a mono uppercase kicker over the `h1`. |
| `.jt-page-narrow` | Centres a single-purpose, one-action page (create/edit/move/decompose, change password, assign a role…) as one narrow column, so it doesn't hug the left edge of the wide `.container` on large screens. Not for pages that mix a form with a wide table or list. |
| `.jt-context` | A quiet subtitle line naming the record/context a page acts on (`label` + `strong` value). |
| `.jt-card` | Generic white surface card. |
| `.jt-grid` | Auto-fit responsive card grid. |
| `.jt-metrics` / `.jt-metric` | The signature metric read-out: a large tabular number under a mono label, with an orange→gold baseline. Used for cost figures. |
| `.jt-form-card` | Framed card wrapping a primary form (login, create, edit, filters). |
| `.jt-toolbar` | A wrapping cluster of action buttons/forms, so actions read as a control group. |
| `.status-pill` (`-ready` / `-blocked`) | A pill for readiness/achievement state, led by a stop/go sign — see below. Add `.status-pill--icon` for the sign alone. |
| `_IconSprite.cshtml` | Every `<symbol>` in the app, rendered once by `_Layout`. Draw a glyph with `<svg><use href="#jt-icon-…"></use></svg>`; never redefine a symbol in a page. |
| `.jt-list` | Bare `<ul>` rendered as a stack of chip rows (prerequisites, siblings, blockers). |
| `.jt-tag` | Inline mono chip for an identifier outside a table. |
| `.jt-empty` | Dashed empty-state panel ("None.", "No jobs to show."). |
| `.jt-notice` | Framed informational/denied panel. |
| `.jt-auth` | Centred column for sign-in / sign-out / access screens. |
| `.table` | Ledger table — firm header rule, hairline rows, a warm orange hover wash, horizontal scroll on narrow viewports. Add `.jt-id` / `.jt-amount` to cells for mono tabular rendering. |
| `<dl>` | Record card — a label/value grid with a burnt-orange spine, collapsing to one column below 30em. |
| `.jt-tree-cell` (+ `.jt-tree-guide`, `.jt-tree-label`, `.jt-tree-icon`) | The Browse subtree's description cell, styled as a file-manager listing — see below. |
| `.jt-col-secondary` | A table column that is dropped below 768px. See "The tree row" for what qualifies. |
| `.jt-icon-button` (+ `.jt-backdate-trigger`) | A small square glyph button, for a rare action sitting beside a common one (Backdate, next to Start/Finish). Always carries a visually-hidden name. A `.jt-backdate-trigger` toggles the matching `.jt-backdate-row`/`.jt-backdate-panel` open via `aria-expanded`/`aria-controls` (site.js), rather than opening a floating popup. |
| `.jt-backdate-row` / `.jt-backdate-panel` | The tinted, hidden-by-default expansion a backdate trigger reveals: a full-width table row (`.jt-backdate-row`, one cell spanning the table via `colspan`) or a block under a toolbar (`.jt-backdate-panel`), each wrapping the shared `.jt-backdate-form` (label + `datetime-local` input + submit). |

### Stop and go

Readiness reads as a crossing signal, everywhere it appears: a **red raised palm** when a job is
blocked, a **green go sign** when it is ready. This is the app's whole vocabulary for "may this
proceed" — Browse's readiness field, each prerequisite in the Requires list, inherited blockers,
Awaiting-progress rows, and the `_Readiness` partial all use the same pair, and any new surface
reporting the same fact must use it too rather than inventing a word or a colour.

Two rules keep it compact and honest:

- **A worded pill where there is room, the sign alone where there is not.** A record card or a
  standalone statement keeps its label ("Blocked", "Ready — every prerequisite is satisfied");
  a marker repeated once per row or list item uses `.status-pill--icon`, which drops the word to a
  visually-hidden span. A state costs a glyph's width per row, never a column.
- **Blocked is red** (`--jt-red-*`), not the amber it used to be: the pill carries a red palm, and a
  red hand on an amber ground reads as two states at once. Red is otherwise the error colour, but a
  blocked job is the nearest thing to a stop the domain has, and the error components
  (`.jt-notice--error`, `.jt-eyebrow--error`) never sit beside a status pill.

The plain dot lead survives only on pills that report something *other* than readiness — "Active
since…", which is a running session, not a state that stops or permits work.

### The tree row

Browse's subtree table is the one place hierarchy is drawn rather than described, so it reads like a
file manager's list view: each row is indented one `--jt-tree-indent` step per level, an `└` elbow
connects it to its parent, and a 16px glyph names its kind — an opened container for a root, a
closed one for a branch, a leaf for a leaf. Same Root/Branch/Leaf vocabulary as the `.jt-kind` chip,
drawn instead of spelled.

Containers are `--jt-folder-600` (the container blue, used nowhere else) and a leaf is
`--jt-green-700`, taking the metaphor literally so structure and terminal work separate at a glance.
Neither is a state: both are glyphs beside a name, and the status pill remains the only thing that
reports state.

The row carries facts a column used to: there is no Kind column (the glyph names it) and no Archived
column (a `.jt-tree-flag` archive glyph sits beside the name on the few rows that are archived,
rather than a column of "no" against every row that isn't).

**Reflow.** Below 768px the table keeps only the name and the work controls; `.jt-col-secondary`
marks owner, priority, cost, and the span bar, and they are `display: none` at that width. Six
columns at 320px would squeeze the name to a couple of characters per line and push the page into
the horizontal scroll WCAG 1.4.10 forbids. Every dropped column is one tap away on the row's own
page, and the same class does the same job in the flat search-results table. Use it for any column
that is genuinely redundant at phone width — not for content that assistive tech still needs, which
belongs in a visually-hidden span instead.

**Row geometry never uses a `style` attribute.** The CSP is `style-src 'self'` with no
`'unsafe-inline'`, so an inline style is dropped by the browser and whatever it positioned renders
at zero size — silently, with only a console warning. The span bar carries its per-row geometry as
SVG `x`/`width` presentation attributes on a rect inside a `viewBox="0 0 100 6"`, which the CSP does
not police.

Three details are load-bearing:

- **The guide is absolutely positioned** inside the cell, so its rails run the full row height and
  meet the rows above and below as one unbroken line per ancestor level. The indent is therefore the
  cell's `padding-left`, set by a per-depth attribute rule bounded by `JobSubtreeLimits.HardMaxDepth`.
- **Glyphs are drawn with `<use>` from one per-page sprite**, and a `<use>` clone lives in a shadow
  tree that document CSS cannot select into. Colour reaches it only by inheriting `fill` from the
  host `<svg>`; every other paint is a presentation attribute on the symbol itself.
- **Rails, elbows, and glyphs are `aria-hidden` decoration** in the slate leg (the reserved
  hierarchy-scaffolding colour), so no contrast floor applies to them. Depth and kind reach a screen
  reader through each row's visually-hidden "Level N, `<kind>`." label instead.

## Responsiveness

Mobile-first and verified down to phone widths:

- The header collapses to the Bootstrap toggler below the `sm` breakpoint.
- Card grids (`.jt-grid`, `.jt-metrics`) are `auto-fit` and collapse to a single column.
- `<dl>` record cards drop to a single stacked column below 30em.
- `.table` scrolls horizontally inside its own box (overflow is set on the table element, so no
  `.table-responsive` wrapper is needed in markup).
- Type and spacing scale up at the 768px breakpoint.

## Working on the UI

1. Prefer a token or an existing primitive over new bespoke CSS. If a page needs something new,
   add a named component to `site.css` with a comment, and reuse it.
2. Keep Bootstrap vendored files untouched; theme through `--bs-*` overrides and explicit
   `.btn-*` custom properties (Bootstrap 5.3 compiles some values to literal hex at build time, so
   the `--bs-primary`/`--bs-link-color` overrides do not reach `a` and `.btn-primary` on their own —
   those two carry explicit rules; see the comments in `site.css`).
3. Re-run the end-to-end axe scan after any colour, contrast, or structural change.
4. No real PII in screenshots or fixtures (`CLAUDE.md`).
