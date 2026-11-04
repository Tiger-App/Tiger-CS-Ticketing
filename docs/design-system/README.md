# Tiger Ticketing — unified design system

Ticket Details is the approved visual baseline. Every module now shares its
tokens, shell and type scale from `src/TigerCS.Web/wwwroot/css/site.css`.

## Tokens

| Token | Value | Use |
|---|---|---|
| `--tiger-gold` | `#E7B95E` | The one brand accent: primary buttons, active nav underline, selected tabs, highlights |
| `--tiger-gold-hover` | `#DDAC47` | Hover / pressed state of the same gold |
| `--tiger-gold-tint` / `-pale` / `-line` | wash / faint wash / hairline | Selected nav background, tab counts, info bands |
| `--tiger-on-gold` | `#2A1F06` | Text on gold fills (9.9:1) |
| `--tiger-gold-ink` | `#7A5206` | Gold-family *text and icons* on white (6.9:1); `#E7B95E` itself is only 1.9:1 on white |
| `--page-max` / `--page-pad-x` / `--page-pad-y` | 1600px / 28px / 18px | One content width and margin for every page, and the header |

The legacy `--color-gold*` names alias these, so no rule can drift to a second
gold. Semantic colours (info, progress, success, warning, critical, secondary)
and ticket-status badges are unchanged.

## Type scale

| Token | Size | Role |
|---|---|---|
| `--text-2xl` | 24px | Main page title |
| `--text-xl` | 22px | Record title (Ticket Details) |
| `--text-lg` | 16px | Section heading |
| `--text-md` | 15px | Card heading |
| `--text-base` | 14px | Body |
| `--text-sm` | 13px | Tables, buttons, inputs |
| `--text-xs` / `--text-2xs` | 12px | Labels, metadata, badges — the floor |

Hard-coded sizes were remapped onto this scale (previously ~40 distinct values,
including rem/em and fractional pixels). `<small>` no longer falls back to the
browser's 10.8px. Body line-height is 1.55; headings 1.2; there is no
word-spacing and tracking is only negative on titles / positive on uppercase
micro-labels.

## Shell

`_Layout` renders the one-row header (logo · navigation · search · account) on
every page. A page can still opt out with `ViewData["CompactShell"] = false`.
The Administration sub-navigation sits under it, aligned to the same width.

## Modified files

- `wwwroot/css/site.css` — tokens, scale, gold, shell, page width, mobile page-header wrapping
- `wwwroot/css/collections-*.css`, `review.css` — type scale; review page uses the official gold
- `Pages/Shared/_Layout.cshtml`, `Pages/Shared/_Header.cshtml` — one shell for all pages
- `TigerCS.Tests/Web/AppTypographyTests.cs` — new scale ranges and a "one official gold" guard

No backend, API, schema, workflow, permission, Genesys, CRM, SLA or Collections logic was touched.

## Verification

Chromium at 1440, 1280 and 390px over 12 pages rendered through the real Razor
pipeline with the test fakes: no horizontal page overflow, no text under 12px.
Before/after: `screenshots/` (1440px for Ticket Details, Dashboard, Tickets,
Customers, Customer Profile, Receivables, Administration, Users, Team
Performance; 390px for Ticket Details and Tickets). The `before_*` images are
rendered with the previous CSS and shell.

## Remaining inconsistencies

- Customer Profile and Ticket Details tabs share the gold underline but not the exact tab size/spacing.
- Collections filter bar's tower picker control is styled by `collections-*.css` and still differs slightly from the standard select.
- Tickets list panel has more inner padding than the Ticket Details cards.
- Login / Access Denied (own layout) keep their hero type sizes; Collections receivables notices keep their amber warning tints (semantic, not brand).
- Modals other than the Ticket Details Actions modals were verified by CSS inheritance, not individually screenshotted.

## Tower multi-select (Collections)

`Pages/Shared/_TowerMultiPicker.cshtml` + `collections-tower-multi.css` + `collections-tower-multi.js`
(Receivables, Campaigns and Tower Assignments share it). Searchable, 300-340px panel above the
table, "All towers" / "N towers selected", code and name in aligned columns, 240px scrolling list,
gold (`--tiger-gold`) checkboxes / selected rows / focus rings, compact "Select all" / "Clear all"
text actions. Keyboard: Down opens and enters the search, Down/Up/Home/End walk the options, Space
toggles, Escape closes. Searching only hides rows, so ticked towers stay ticked and are still
submitted. All CSS is scoped to `.tower-multi`. Screenshots: `screenshots/tower-multiselect/`.
Tower Assignments keeps its own empty label ("No towers"): there, nothing ticked means none, not all.
