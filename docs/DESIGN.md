# SEP design system

How the SEP desktop UI is put together, so the next change stays coherent. The look follows macOS Tahoe / iOS 26
("Liquid Glass") as far as WPF can render it cheaply. The sidebar is a floating glass pane over Mica. The content is
opaque, built from Apple's semantic colours, with white (or #1C1C1E) cards on a grouped background. Inset grouped
lists follow the Settings app. There is one blue for actions, and colour appears only where it means something.

Everything lives in `SEP.App/Themes/` and `SEP.App/Controls/`:

| File | Holds |
|---|---|
| `Themes/SepTheme.Dark.xaml`, `Themes/SepTheme.Light.xaml` | Colour and brush tokens, plus the WPF-UI key overrides. Same keys in both files; `ThemeService` swaps them at run time. |
| `Themes/SepStyles.xaml` | Theme-independent tokens (fonts, radii, spacing), the type ramp and shared styles: surfaces, grouped lists, buttons, segments, widget tiles, the Apple switch, toast and tooltip templates. |
| `Themes/Generic.xaml` | Default looks of the SEP controls (`Card`, `SegmentedPanel`, `Badge`, `IconTile`, `SectionHeader`, `EmptyState`, `SettingCard`, `NavItem`). It is also the theme dictionary, so it uses DynamicResource only. |
| `Themes/ProjectCard.xaml` | The project card, the glossy monogram template and the shared project context menu. |
| `Controls/Card.cs` | A `Border` that paints a soft shadow with gradients (no effect). |
| `Controls/SeparatedStackPanel.cs` | Vertical stack that draws inset hairlines between rows (grouped lists). |
| `Controls/SegmentedPanel.cs` | Segmented control: grey track and a raised thumb that slides to the checked segment. |
| `Controls/CardGridPanel.cs` | Responsive equal-width card grid. |
| `Behaviors/SpringEase.cs`, `Behaviors/ToggleMotion.cs` | The spring curve every motion uses; switch motion that plays only on real toggles. |

## Rules that keep it working

1. **Brushes are always `{DynamicResource ...}`.** The theme dictionary is replaced at run time, and a StaticResource
   brush keeps the old theme. Every key goes into **both** theme files: the static check fails on a key that exists in
   only one of them.
2. **`{StaticResource ...}`** is only for styles, templates, converters, fonts, radii and thicknesses that are
   defined earlier in the merge order (Wpf.Ui → SepTheme → SepStyles → Generic → page dictionaries). SepStyles must not
   use BasedOn with a style from Generic, which is merged later.
3. **Text only through resources** (`{loc:Tr Key}`). Add the key to `Strings.resx`, `Strings.zh-CN.resx` and
   `Strings.Designer.cs`. Project content is English; the zh-CN file carries the Chinese.
4. **No effects on repeated items.** No `DropShadowEffect` or `BlurEffect` on cards, rows or chips. Light-theme depth
   comes from `controls:Card`, which draws its shadow with gradient brushes. An effect is allowed only on one transient
   or single surface (toast, tooltip), and then on an empty layer *behind* the content. An effect on an element turns
   off ClearType for the text inside it.
5. **Brush changes are trigger setters, not storyboards** (a storyboard cannot target a theme brush). Storyboards
   animate transforms and opacity only.
6. **Nothing animates while idle.** No `RepeatBehavior="Forever"` except the scan skeleton, which stops when the scan
   ends.
7. **The backdrop is set only by `WindowBackdropType="{x:Static sepsvc:ThemeService.Backdrop}"`** in each window's
   XAML, never on a loaded window. Where `ThemeService.MicaSupported` is false (Windows 10, Remote Desktop,
   `SEP_NO_MICA=1`), surfaces that would show Mica fall back to `SepWindowBrush`.

## Colour

Apple's semantic colours, in use as on iOS / macOS. Most of the UI is neutral grey. Colour is used for meaning
(status, presence, warnings) and for the one accent.

| Token | Dark | Light | Use |
|---|---|---|---|
| `SepPage` | #000000 | #F2F2F7 | Grouped background of the content area. |
| `SepSurface` | #1C1C1E | #FFFFFF | Cards, grouped rows. |
| `SepSurface2` | #2C2C2E | #F2F2F7 | Inner surfaces: wells, nested panels. |
| `SepSurfaceHover` / `SepSurfacePressed` | #252527 / #2C2C2E | #F5F5F7 / #EBEBF0 | Row and card hover / press. |
| `SepFill` (+`Hover`, `Pressed`) | white 10/14/18 % | black 5/8/12 % | Secondary buttons, chips, icon-button hover, keycaps. |
| `SepTrack` / `SepSegment` | tertiary fill / #636366 | tertiary fill / white | Segmented control track and thumb. |
| `SepInput` | white 7 % | white | Text fields (with a hairline). |
| `SepSheet` / `SepSheetGroup` | #1C1C1E / #2C2C2E | #F2F2F7 / white | Dialog background and its grouped form rows. |
| `SepPopup` (+`Border`) | #2C2C2E | white | Menus, drop-downs, tooltips, toasts. |
| `SepSeparator` (= `SepDivider`) | #38383A | #C6C6C8 | Hairlines between grouped rows and blocks. |
| `SepBorder` / `SepBorderStrong` | white 14 / 28 % | black 12 / 24 % | Field and chip hairlines; drop-zone dashes. |
| `SepCardBorderBrush` | lit rim, brighter on top | black 4–6 % hairline | Card edge. |
| `SepText` / `SepText2` / `SepText3` | #FFFFFF / #AEAEB2 / #98989F | #1D1D1F / #55555A / #6C6C70 | Label, secondary, tertiary. Text3 stays ≥ 4.5:1 up to #2C2C2E (dark) and on white, #F2F2F7 and the hover wash (light). |
| `SepAccent` | #409CFF | #0040DD | Accent **text and glyphs**: the increased-contrast systemBlue. |
| `SepAccentStrong` (+`Hover`, `Pressed`) | #0071E3 | #0071E3 | **Fills with white text** (Primary, the selected sidebar row): 4.7:1. Hover darkens. |
| `SepTint` | #0A84FF | #007AFF | systemBlue itself, for what carries no text: focus rings, switches, selection rings, check boxes, progress. |
| `SepAccentSoft` (+`Hover`) | systemBlue 20 % | systemBlue 10 % | Tint behind accent text (tinted buttons, accent badges, the batch-selected card). |
| `SepSuccess/Warning/Danger/Info/Purple` + `*Soft` | system colours | increased-contrast, a step darker | Tone text/glyph colour + soft tint. Light values: green #1B7535, orange #B83000, red #C40012, cyan #006694, purple #8944AB. Each keeps ≥ 4.5:1 on white, on #F2F2F7 and on its own tint. Apple's #248A3D is only 4.4:1 on white. |
| `SepDangerFill` | #E0281E | #D70015 | White-text destructive fill (also WPF-UI's `PaletteRedColor`). |
| `SepTile*Brush` | system colours | system colours | Icon tiles: Blue, Indigo, Purple, Pink, Red, Orange, Yellow, Green, Teal, Cyan, Gray, each a faint top-light gradient. The glyph is white. |
| `SepShadowColor` / `SepShadowHoverColor` | transparent | black 10 / 16 % | Soft card shadow; none in dark. |

Contrast was checked for every text token against every surface it sits on. Keep 4.5:1 for any new text colour.
Placeholders and disabled text are exempt.

WPF-UI's own controls follow through key overrides in both theme files:
- `AccentButton*`: solid accent, white text, no elevation stroke.
- `Button*` and `ControlElevationBorderBrush`: a borderless grey fill.
- `TextControl*`: a hairline well, with systemBlue when focused.
- `ComboBox*`: grey pop-up buttons with a popover list.
- `CheckBoxCheck*`: a systemBlue box with a white tick.
- `ContextMenu*`, `Flyout*`, `ToolTip*`, `SnackBar*`: popover colours.
- `PaletteRedColor`: the danger fill.

`ThemeService` passes systemBlue to all four WPF-UI accent slots. The one-colour overload would lighten it for dark mode.

## Materials and depth

* **Sidebar glass.** The pane floats 8 px from the window edge, radius 18, and holds the following:
  - `SepSidebarBrush`: white at 8 % in dark, 65 % in light, so Mica shows through.
  - A hairline `SepSidebarBorderBrush`.
  - A 1 px `SepSpecularBrush` highlight along the top edge.
  - No shadow.
  The session card inside is a second, lighter glass layer (`SepSidebarCard`) with its own specular line.
* **Content** is opaque `SepPage`. In dark mode a faint `SepContentEdgeBrush` hairline separates it from the Mica strip.
  In light mode the edge is invisible and the sidebar floats on one light field.
* **Light theme:** cards cast a soft shadow (`controls:Card`: a wide ambient band plus a tight contact shadow, both drawn
  from gradients). The hero gets a deeper one (`SepHeroCard`). Grouped lists stay flat like the Settings app.
* **Dark theme:** no shadows. Depth is lighter surfaces (black → #1C1C1E → #2C2C2E), and cards get a faint lit rim.

## Typography

Segoe UI Variable: the Display cut for titles and numbers, the Text cut for everything else, Cascadia for codes and
paths. SF Pro is not bundled because its licence forbids non-Apple platforms.

| Style | Size / weight | For |
|---|---|---|
| `SepDisplay` | 34 Bold | The one big name or value of a screen (overview hero). |
| `SepLargeTitle` (= `SepPageTitle`) | 30 Bold | Page titles. |
| `SepTitle` | 20 SemiBold | Hero and empty-state titles. |
| `SepSubtitle` | 19 Bold | Section titles on a page (a library, a group, "Quick launch"); `SectionHeader` uses it. |
| `SepSheetTitle` / `SepSheetSubtitle` | 17 SemiBold / 13 Text2, centred | Dialog (sheet) header. |
| `SepSectionTitle` | 15 SemiBold | Titles inside a card. |
| `SepBodyStrong` / `SepBody` | 13 SemiBold / 13 | Item names / body text. |
| `SepPageSubtitle` | 13, Text2, wraps | Page subtitles, explanations. |
| `SepGroupHeader` / `SepGroupFooter` | 13 SemiBold Text2 / 12 Text3 | Above / below a grouped list. |
| `SepMuted` / `SepLabel` | 12 Text2 / 12 Medium Text2 | Secondary lines / field labels. |
| `SepCaption` / `SepMono` | 11 Text3 | Captions / paths and codes. |
| `SepEyebrow` | 13 SemiBold accent | Small line above a hero value. |
| `SepNavSectionLabel` | 11.5 SemiBold Text3, title case | Sidebar section headers. |
| `SepKpiValue` / `SepNumber` | 34 Bold / 12, tabular figures | Widget numbers / counts. |

WPF-UI controls get 13 px text (`ControlContentThemeFontSize`). Pages set `Style="{StaticResource SepPage}"`, because a
Frame stops inherited text properties.

## Shape, spacing, layout

* Radii (`SepStyles.xaml`):
  - `SepRadiusXL` 22: hero.
  - `SepRadiusL` 18: cards, widgets, the sidebar pane, project cards.
  - `SepRadiusGroup` 14: grouped lists, sheet groups.
  - `SepRadiusM` 12: inner surfaces.
  - `SepRadiusS` 8: buttons, fields (also WPF-UI's `ControlCornerRadius`).
  - `SepRadiusPill`: capsules.
  - Popups use 12.
* **Concentric corners:** a shape inset by *p* inside a parent of radius *R* gets *R − p*. Examples: the sidebar rows
  (18 − 8 = 10), the segmented thumb (track − 2), the session card.
* Spacing scale 4 / 8 / 12 / 16 / 24 / 32 / 40 (`SepSpace1..7`).
* **Page frame:**
  - The header uses `SepPageHeaderMargin` (40, 24, 40, 24): a large title, the subtitle under it, and actions on the right.
  - The scrolling content uses `SepPageContentMargin` (40, 0, 40, 40), set *inside* the ScrollViewer so the scroll bar
    sits at the window edge.
  - The status line uses `SepPageStatusMargin`.
  - Form-like pages are capped at `SepFormMaxWidth` (880).
* Section gap 32, card gap 16, card padding 18.

## Components

* **`controls:Card`**: the card surface (white / #1C1C1E, radius 18, hairline, soft shadow in light). It is a `Border`,
  so drag-drop highlights work on it. `SepCard` is the same surface as a plain Border without the shadow, and
  `SepHeroCard` is the one hero of a page.
* **Inset grouped list** (the Settings app):
  - A `SepGroup` container (radius 14, clipped) holding a `controls:SeparatedStackPanel`, either directly or as an
    ItemsPanel.
  - `SepGroupStack` starts the hairlines past a 26 px row icon (inset 56); `SepGroupStackPlain` starts them at 16.
  - Rows are borderless: `SepGroupRow` lights on hover, `SepGroupRowStatic` does not, and `SepGroupRowButton` is a
    whole-row button with a trailing `ChevronRight` in Text3.
  - Rows end in a value, a chevron or a control.
  - `SepGroupHeader` goes above the group and `SepGroupFooter` below it.
* **`SettingCard`**: one settings row (Small vivid tile, title, description, control). It has no surface of its own and
  lives in a grouped list.
* **`IconTile`**: a squircle with a white glyph on the tone's system colour (Small 26, Medium 32, Large 56; radius about
  23 % of the size). `IsSubtle` makes it a soft tint with the glyph in the tone colour, used in empty states and inline
  hints. Tones: Accent → blue, Success → green, Warning → orange, Danger → red, Info → teal, Purple → purple,
  Neutral → grey.
* **Monogram**: the item's code on its `PaletteGradientConverter` colour (lighter top-left), with a `SepAvatarGloss`
  top light. Use the `ProjectMonogram` template for projects.
* **`Badge`**: status. `IsPlain` gives a dot and words in Text2, the quiet default on cards and rows. The plain capsule
  (soft tint, tone text) is for states that must stand out: online, locked, cached, warnings. `IsSolid` is the one
  emphasised state (the active project).
* **Chips**: `SepChip` grey capsules for data (tags). A card shows three and then "+n" (`TakeConverter`,
  `OverflowCountConverter`).
* **Buttons**:
  - WPF-UI `Primary`: solid accent fill, white text. Use it for the main action of a page or section.
  - `Secondary`: a borderless grey fill. Use it for the rest.
  - `SepCapsuleButton` / `SepCapsuleSecondaryButton`: capsules for hero and sheet actions, e.g. the blue
    "Launch E3D".
  - `SepTintedButton`: accent text on a soft accent wash, for quiet actions repeated on cards.
  - `SepIconButton` / `SepDangerIconButton`: borderless icons with a grey wash on hover.
  - `SepLinkButton`: small accent text.
  - `SepTrack`: groups rare icon actions in a grey capsule.
  - Every ui:Button presses in to 97 % (icon buttons to 92 %).
* **Segmented control**:
  - `controls:SegmentedPanel` with RadioButtons styled `SepSegmentItem`.
  - The panel draws the grey track and a raised thumb (white with a soft shadow in light, #636366 in dark). The thumb
    slides to the checked segment on a spring.
  - Segments are equal width unless `IsUniform="False"`.
  - Check a segment from data with
    `IsChecked="{Binding Filter, Converter={StaticResource EqualsConverter}, ConverterParameter=X, Mode=OneWay}"` and keep
    the Click handler that sets the value.
  - Page tab strips use the same control.
* **Switch**: every `ui:ToggleSwitch` uses the Apple template (40 × 24 capsule, white knob with soft rings, grey → systemBlue
  when on). `ToggleMotion` slides the knob and fades the fill on real toggles only, so a list of switches never animates
  while it fills.
* **Widget tile** (`SepTileButton`): a clickable card (overview widgets). It holds a small vivid tile, a quiet label, a
  big `SepKpiValue` number and a caption. It rises 2 px on hover and presses in to 97 %.
* **`SectionHeader`**: optional small tile, a 19 Bold title (15 SemiBold with `IsCompact`), a caption, and right-aligned
  content such as a link or a badge.
* **`EmptyState`**: a large subtle tile, a 17 SemiBold title, a one-line hint and a call to action. A missing library
  list is a dashed drop zone, because the page takes folder drops.
* **Project card** (`ProjectCardTemplate`):
  - A widget card holding:
    - an optional batch check box;
    - the glossy monogram;
    - the title, with "evars name · library" under it;
    - the star;
    - the status as dot + words, with capsules only for Active, Online, Locked and Cached;
    - the owner and up to three tags;
    - a quiet action row (tinted "Switch & launch", then folder and more icons).
  - It has no fixed width: hosts put it in a `CardGridPanel` (MinItemWidth about 300). Its margin is the grid gap.
  - Hover lifts it 2 px with a stronger shadow. A batch-selected card turns blue-tinted with a 2 px systemBlue ring.
* **Sidebar** (`NavItem` in `MainWindow`):
  - Like macOS System Settings: a 22 px coloured squircle per page (Overview blue, Workbench indigo, My Projects
    orange, Plug-ins purple, Users green, Tools teal, Settings grey), the label, and plain grey counts.
  - Section headers are small, semibold and grey, in title case.
  - One shared highlight slides under the checked row. It is accent blue with white text while the window is active,
    and grey with label-coloured text when the window is in the background.
* **Sheets (dialogs)**:
  - An opaque `SepSheet` background.
  - A centred header: a vivid tile, `SepSheetTitle` and `SepSheetSubtitle`.
  - Grouped form rows on `SepSheetGroup`.
  - Errors in a soft danger-tinted row.
  - Buttons bottom-right: Cancel, then the primary (destructive = `Danger`).
  - Widths: 480 (prompt), 560 (standard), 640 (wide). Long forms cap their height at the screen and scroll, so the
    buttons stay on screen.
* **Toasts**: rounded popover banners (radius 16) whose symbol is tinted by the outcome: green, orange, red, or blue
  for information.
* **Title bar**: the native Windows caption buttons (no traffic lights), a quiet breadcrumb and the busy ring.

## Status semantics

State is always **dot + words** (or a capsule with words), never colour alone.

| State | Tone |
|---|---|
| Project status: in progress / completed / on hold / archived | Success / Info / Warning / Neutral (`StatusToToneConverter`; the overview chart uses the same colours) |
| Active project | Accent, solid capsule |
| Presence: online / in use | Purple capsule with the people glyph; free = Success dot |
| Library reachable / unreachable | Success dot / Danger capsule (the error is the tooltip) |
| Offline (cached library) | Danger |
| Plug-in enabled / disabled | Success dot / Neutral dot |
| Locked | Warning capsule with the lock glyph |
| Health check passed / warning / failed / skipped | Success / Warning / Danger / Neutral tiles |
| Category | Plain badge whose dot is the category colour (`DotBrush`) |

## Motion

Spring-like ease-out (`SpringEase`: a damped spring with about 1 % overshoot) over 200–340 ms, on transforms and
opacity only.

* Page change: fade 200 ms and settle 8 px upward on the spring.
* Sidebar: the selection highlight slides to the new row (340 ms). It snaps on the first placement and on resize.
* Segmented control: the thumb slides and resizes to the checked segment (300 ms). It snaps when off screen.
* Switch: the knob slides (280 ms) and the fill fades (180 ms), on user toggles only.
* Press: buttons 97 %, icon buttons 92 %, widget tiles 97 %.
* Hover: cards and widget tiles rise 2 px with a stronger shadow, and rows get a wash.
* Card entrance: fade with an 8 px spring settle, on the first fill of a list only (`CardEntrance`).
* Toasts slide up on the spring. The scan skeleton pulses only while a library scans with nothing to show yet.

## Checklist for a new screen

1. Large title header, subtitle and actions right; content inside the page margins.
2. Lists of settings or things → an inset grouped list. Things with a picture and actions → cards in a `CardGridPanel`.
3. One Primary action per area. Repeated quiet actions → tinted capsule or icon buttons.
4. Status → `Badge IsPlain` (dot + words). Colour only where it means something.
5. Both themes: only DynamicResource brushes that exist in both theme files, and text at ≥ 4.5:1.
6. Run the static XAML check (resource keys, theme parity, string keys, element names) and build.
