# SEP design system

How the SEP desktop UI is put together, so the next change stays coherent. The look is Windows 11 Fluent 2:
Mica window, layered surfaces, quiet borders, one accent, and status shown as colour + dot + text.

Everything lives in `SEP.App/Themes/`:

| File | Holds |
|---|---|
| `SepTheme.Dark.xaml`, `SepTheme.Light.xaml` | Colour and brush tokens. Same keys in both files; `ThemeService` swaps them at run time. |
| `SepStyles.xaml` | Theme-independent tokens (fonts, radii, spacing) and the shared styles. |
| `Generic.xaml` | Templates of the SEP controls (`Badge`, `IconTile`, `SectionHeader`, `EmptyState`, `SettingCard`). Also the theme dictionary, so DynamicResource only. |
| `ProjectCard.xaml` | The project card template and the shared project context menu. |

## Rules that keep it working

1. **Brushes are always `{DynamicResource ...}`.** The theme dictionary is replaced at run time; a StaticResource
   brush keeps the old theme. Every key goes into **both** theme files.
2. **`{StaticResource ...}`** is only for styles, templates, converters, fonts, radii and thicknesses that are
   defined earlier in the merge order (Wpf.Ui → SepTheme → SepStyles → Generic → page dictionaries).
3. **Text only through resources** (`{loc:Tr Key}`): add the key to `Strings.resx`, `Strings.zh-CN.resx` and
   `Strings.Designer.cs`. Project content is English; the zh-CN file carries the Chinese.
4. **No effects per item.** No `DropShadowEffect` / `BlurEffect` on anything repeated (cards, rows, chips).
   Depth comes from the card stroke (below). A shadow is allowed on a single hero surface, a dialog, or the drag
   ghost, and then on an empty border *behind* the content: an effect on an element turns off ClearType for the
   text inside it.
5. **Cheap motion only** (see Motion). Nothing animates continuously on many elements.

## Colour tokens

Semantic, not literal. Each `*Color` has a `*Brush`.

| Token | Use |
|---|---|
| `SepPage` | Window/dialog background behind Mica. |
| `SepSurface` | Cards, rows, the raised layer. |
| `SepSurface2` | Inner surfaces (a well inside a card), nested rows, neutral tints. |
| `SepSurfaceHover` / `SepSurfacePressed` | Hover and press washes. |
| `SepTray` | The recessed area a card list sits in inside a group card (library, my-projects group). |
| `SepTrack` / `SepSegment` | Segmented control track and its raised selected segment. |
| `SepInput` | Text-box like wells. |
| `SepBorder`, `SepBorderStrong`, `SepDivider` | Strokes: normal, emphasised (hover, dashed drop zone), hairline separators inside cards. |
| `SepCardEdge` + `SepCardBorderBrush` | The card stroke: border colour that turns into the darker edge along the bottom. Use `SepCardBorderBrush` for every card-like surface. |
| `SepText`, `SepText2`, `SepText3` | Primary, secondary, tertiary text. Text3 keeps ≥ 4.4:1 on Surface2 in both themes. |
| `SepAccent`, `SepAccentStrong`, `SepAccentSoft`, `SepOnAccent` | Accent text/strokes, fills (also WPF-UI's accent), tints, text on accent fills. |
| `SepSuccess/Warning/Danger/Info/Purple` + `*Soft` | Tone pairs: strong colour for text/dots/icons, soft tint for backgrounds. |
| `SepAccentGradientBrush` | Hero actions only (title-bar Launch, overview hero, app monogram). Text on it: `SepOnAccentBrush`. |
| `SepHeroBrush`, `SepHeroBorderBrush` | The one hero card of a page. |

Dark: borders carry structure; lighter = higher. Light: white cards on a cool grey page; slightly stronger borders
and the darker card edge carry depth. Light tone colours are one step darker than the usual 600 shades so badge
text stays near 4.5:1 on its tint.

## Type ramp (`SepStyles.xaml`)

Display/Title use `SepDisplayFont` (Segoe UI Variable Display → Segoe UI); everything else `SepTextFont`
(Segoe UI Variable Text → Segoe UI); codes and paths `SepMonoFont` (Cascadia).

| Style | Size / weight | For |
|---|---|---|
| `SepDisplay` | 30 SemiBold | The one big value of a screen (overview hero). |
| `SepPageTitle` | 24 SemiBold | Page titles. |
| `SepTitle` | 20 SemiBold | Dialog titles (18 in dialogs), hero titles. |
| `SepSubtitle` | 16 SemiBold | Section headers between cards (`SectionHeader` uses it). |
| `SepSectionTitle` | 14 SemiBold | Titles inside a card. |
| `SepBodyStrong` / `SepBody` | 13 SemiBold / 13 | Item names / body text. |
| `SepPageSubtitle` | 13, Text2, wraps | Page and dialog subtitles. |
| `SepMuted` | 12, Text2 | Secondary lines. |
| `SepLabel` | 12 Medium, Text2 | Field labels. |
| `SepCaption` / `SepMono` | 11, Text3 | Captions / paths and codes. |
| `SepEyebrow` | 12 SemiBold, accent | Small line above a hero value. |
| `SepKpiValue` / `SepNumber` | 28 / 12, tabular figures | Compared numbers (KPIs, counts). Badges are tabular too. |

Pages set `Style="{StaticResource SepPage}"` (foreground, text font, 13 px): a Frame stops inherited text
properties, so a page has to set them itself.

## Spacing, radius, layout

* Radius: `SepRadiusL` 12 (cards), `SepRadiusM` 10 (rows, tiles, inner), `SepRadiusS` 8 (inputs, buttons,
  segments), `SepRadiusXS` 5 (tag chips), `SepRadiusPill` (badges).
* Spacing scale 4 / 8 / 12 / 16 / 24 / 32 (`SepSpace1..6`).
* Page frame: header `SepPageHeaderMargin` (32, 24, 32, 20), scrolling content `SepPageContentMargin`
  (32, 0, 32, 32) *inside* the ScrollViewer so the scroll bar sits at the window edge, status line
  `SepPageStatusMargin`.
* Section gap 24, card gap 12–16, card padding 16. Stacked setting cards are 4 apart.

## Surfaces and components

* `SepCard` (Surface + card stroke, r12, p16) → `SepCardInner` (Surface2, r10, p12). `SepHeroCard` for the one
  hero of a page. `SepRow` for list rows on the page, `SepNestedRow` for rows inside a card, `SepInsetRow` for
  borderless rows inside a card, `SepWell` for read-only value wells, `SepToolbar` for quiet filter bars,
  `SepTrack` behind segments and icon-button groups.
* **Page header**: title (+ count badge / progress ring), subtitle under it, actions right: one primary,
  secondary buttons, rare actions as icon buttons in a `SepTrack` group with tooltips.
* **`SectionHeader`**: small `IconTile` + title (+ caption) + right-aligned content (a `SepLinkButton`, a badge).
  `IsCompact` inside cards.
* **`IconTile`**: soft tone tint + Fluent symbol in the tone colour; `Small` 28, `Medium` 36, `Large` 56. Used for
  section headers, KPI tiles, empty states, settings rows, dialog headers, row leading icons. `SepIconTile` is the
  Border version for custom content (the gradient app monogram).
* **Monogram** (`SepAvatar` + `SepAvatarText`): code / initials in white mono on `PaletteConverter`'s colour for that
  name (deep enough for white text in both themes).
* **`EmptyState`**: icon tile, title, one-line hint, call to action. `IsCompact` in side cards. A missing library
  list is a dashed drop zone (the page takes folder drops).
* **`SettingCard`**: icon tile, header, description, control on the right; stacked under a `SectionHeader`.
* **Dialogs**: empty title bar, header (Medium icon tile + 18 px title + subtitle), 24 px content padding, footer
  on Surface with a top divider and right-aligned buttons (Cancel, then the primary; destructive = `Danger`).
  Widths 480 (prompt) / 560 (standard) / 640 (wide).
* **Buttons**: WPF-UI `Primary` for the main action of a card/section, `Secondary` for the rest,
  `SepIconButton` / `SepDangerIconButton` for tool icons, `SepLinkButton` for small text links,
  `SepAccentGradientButton` for the hero action of a screen only, `SepTileButton` for clickable cards,
  `SepSegment` for segmented filters (selected = raised segment, bound through `SelectedToAppearanceConverter`),
  `SepTabButton` for tab strips (accent indicator on a divider line).

## Status semantics

State is always **dot + pill + words** (`Badge` with `HasDot="True"`), never colour alone.

| State | Tone |
|---|---|
| Project status: in progress / completed / on hold / archived | Success / Info / Warning / Neutral (`StatusToToneConverter`; the overview chart uses the same colours) |
| Active project | Success, solid |
| Presence: online / in use | Purple (with people icon); free = Success |
| Library reachable / unreachable | Success / Danger (error text as tooltip) |
| Offline (cached library) | Danger |
| Plug-in enabled / disabled | Success / Neutral |
| Locked | Warning (lock icon) |
| Health check passed / warning / failed / skipped | Success / Warning / Danger / Neutral (tile + summary strip) |
| Category | Neutral pill whose dot is the category colour (`DotBrush`); cards also get the 4 px category stripe |

Standalone dots use `SepStatusDot` (8 px). Tags are square-ish `SepChip`s (data, not state): a card shows three
and then "+n" (`TakeConverter`, `OverflowCountConverter`).

## Motion

* Page transition: NavigationView `FadeInWithSlide`, 160 ms.
* Card / tile hover: lift 2 px (`TranslateTransform`, 120 ms ease-out) + accent stroke + hover wash. Only the inner
  card moves; the host keeps the hit area still.
* Card entrance: fade + 6 px slide, first fill of a list only (`CardEntrance`).
* Press: icon buttons scale 0.92, the gradient button 0.97, tiles sink to the pressed wash.
* Running E3D: the title-bar dot pulses (opacity 1 → 0.35, 1.6 s loop) only while E3D runs.
* Scanning library with nothing to show: three skeleton cards pulse until the first project or the end of the scan.
* Brush changes are trigger setters, not animations (a storyboard cannot target a theme brush).
