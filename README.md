# SEP — AVEVA E3D Project Manager

Native Windows client (.NET 10, WPF, Fluent design) for managing AVEVA Everything3D projects: scan project libraries, keep project details (category / tags / owner / status / notes), pick "my projects", switch what E3D loads and launch it in one click, manage project users and teams, plug-ins, and the health of the E3D configuration.

SEP — AVEVA E3D 工程管理系统：扫描项目库、维护工程信息、一键切换并唤起 E3D、管理工程用户与团队、插件与环境诊断。

## Layout

| Path | What it is |
|---|---|
| `SEP.App/` | The WPF client (`SEP.exe`). Pages: Overview, Project Workbench, My Projects, Plug-ins, Users & Permissions, Diagnostics & Tools, Settings. |
| `SEP.PluginPanel/` | The E3D plug-in panel: a CAF add-in (net48 / x86) that SEP embeds in `SEP.exe` and deploys into the E3D folder. |
| `ADMIN/E3dAdmin/` | Library + CLI (`e3d-admin`) that drives AVEVA `adm.exe` for user / team administration; referenced by the app. |
| `build.ps1` | Self-contained single-file release `dist\SEP.exe`, plus `dist\Install-SEP.cmd` and `dist\SEP-<version>-setup.zip`; copies the exe to the repo root. |

## Build

Requires the .NET 10 SDK.

```powershell
.\build.ps1
```

The default build is **self-contained**: the .NET runtime travels inside `SEP.exe` (about 65 MB), so it starts on
any 64-bit Windows 10 or 11 PC without installing anything first. `.\build.ps1 -FrameworkDependent` makes a small
exe (about 8 MB) that needs the .NET 10 Desktop Runtime on every PC. `dotnet build SEP.App\SEP.App.csproj -c Release`
is the quick developer build.

Both build `SEP.PluginPanel` first and embed it. It compiles against the AVEVA assemblies of
`D:\AVEVA\Everything3D3.1` (or `-p:E3D_HOME=...`); on a machine without E3D it builds without its CAF entry point,
and that SEP keeps the classic ribbon.

## Install on a PC

Hand out `dist\SEP-<version>-setup.zip` (or the `dist` folder). On the target PC:

- **Install / update:** double-click `Install-SEP.cmd` (or run `SEP.exe --install`). SEP is copied to
  `%LOCALAPPDATA%\Programs\SEP` for the current user – no administrator rights – with a Start menu and a desktop
  shortcut and an *Apps & features* entry, and then starts. Running it again with a newer build updates in place
  (a running SEP is closed first). *Settings → About → Install on this PC* does the same from a copy started
  elsewhere (e.g. from a network share).
- **Uninstall:** Windows *Settings → Apps*, or `SEP.exe --uninstall` (`--quiet` for scripts). Libraries, projects
  and settings in `%APPDATA%\SEP` are kept.
- **First start:** the E3D installation (`install_dir`, `evars.bat`, `evars.init`, `projects_dir`) is detected
  automatically from *Apps & features* and the usual `X:\AVEVA\Everything3D*` folders on every local drive, so a
  new PC can launch E3D without visiting Settings.
- Only one SEP window runs per user and data folder; starting SEP again brings the open window to the front.
- Windows 10, Remote Desktop and VMs without GPU composition get a solid window background instead of Mica
  (`SEP_NO_MICA=1` forces it).

## How it launches E3D

- **Single project** – the project's `evarsXXX.bat` is written into the `SEP MANAGED PROJECTS` block at the end of the local library's `custom_evars.bat`, then E3D is started.
- **Whole library** – for a local library, `projects_dir=` in `evars.bat` / `evars.init` is pointed at the library root. A library on a file server (`\server\share`) is never written to: its projects go into this PC's managed block instead, so E3D shows the same list without SEP touching files other people share.
- **All my projects** – every project in *My Projects* is written into the managed block, so all of them appear in the E3D login window.

A folder is a project when it contains `evarsXXX.bat`; a folder whose sub-folders contain such files is a project library. Local paths and `\\server\share` are supported.

## File-server load and project usage

Network libraries are often on Windows PCs acting as file servers (at most 20 SMB connections) and are reached as
Guest, so SEP keeps its footprint small:

- Automatic scans (start-up, opening a page) cover local libraries only; a network library is listed when you
  press refresh, or once when it was never scanned. Cached results are shown meanwhile.
- SEP reads and writes nothing on a server in the background: no heartbeat or presence files, no lock-file sweeps.
  An idle SEP therefore lets its SMB session time out, which frees the server's connection slot.
- *Online now* / the project badges show which projects are open in E3D **on this PC**, detected locally: the files
  held open by the local E3D processes (`des`, `draw`, `mon`, …) are read from the Windows handle table, and a
  project counts as open when its system database `<code>000\<code>sys` is among them (catalogue projects that E3D
  only reads from are not counted). This works whether or not E3D was started from SEP.
- **Who is in (manual only):** a card's menu (one project), the people button on a library or a My Projects group,
  *Who is in* on the Project Workbench (all projects) and *Who is online* on My Projects check other people too —
  only when clicked, never on a timer. Per project SEP opens `<code>000\<code>sys` read-only without sharing: the
  file server refuses that while any E3D session holds the project, so the project is reported *in use* without a
  handle being created (when free, the file is held for a few milliseconds). For projects in use, the sessions still
  running are read from E3D's own session register in `<code>com`: each record carries the Windows user, computer
  and start time (in that computer's clock) and, while the session runs, the list of databases it has open, which
  E3D clears when the session ends. The badge shows how many sessions are running and its tooltip who they are.
  Records of this PC are checked against its live E3D processes; records of other PCs still marked as running after
  3 days are listed apart as probably crashed (*Admin > Expunge* clears them). Nothing is written on the server.

## E3D plug-in panel

E3D's **SEP** ribbon tab no longer carries every plug-in's buttons. It has one **Plug-ins** button (plus *Find
command*, *Refresh PML* and *SEP*) that opens a docked panel listing every plug-in SEP enabled:

- **Status** per plug-in: *Running* / *Ready* (its commands are registered, its add-in is loaded, its PML is on this
  session's PMLLIB), *Not loaded* (the tooltip lists missing add-ins, commands or PML folders), *Folder missing*.
  A missing component does not establish when the plug-in was enabled. A newly enabled plug-in needs a new E3D
  session; a persistent *Not loaded* state requires checking its deployment and startup log.
- **Buttons** for the commands the plug-in supplies in its `.uic` (its large ribbon buttons by default, at most six);
  the rest are under *More*. Plug-ins without a ribbon get buttons from their launcher macro, forms or macros.
- **Status only** plug-ins have no buttons (VVIEW by default: it is used from the keyboard); their commands stay in
  the card's ⋯ menu and in search. A plug-in can also be hidden.
- **Search** across every command of every plug-in (Enter runs the first, ↑/↓ choose), **Pinned** and **Recent**
  chips at the top.
- **Customize** (pencil): per plug-in Buttons / Status only / Hidden, which commands are buttons, their names, order,
  keyboard shortcuts (Ctrl or Alt with a key, or F1–F12; they work anywhere in E3D, and keys E3D's own commands or
  Windows' text editing already use, such as Ctrl+S, F8 or Ctrl+C, are refused), pins, your own PML commands,
  and a shortcut that opens the panel on its search box. Right-click a button for the same options.

SEP writes the list (`SEP.PluginPanel.json`, next to `SEP.PluginPanel.dll` in the E3D folder) on every plug-in change;
an open panel picks it up by itself. Your layout is per user in `%APPDATA%\SEP\plugin_panel.user.json` and is shared
by all E3D sessions. Log: `%LOCALAPPDATA%\SEP\plugin_panel.log`. A plug-in can set its default with an optional
`sep-plugin.json`: `{ "panel": { "mode": "status", "description": "…" } }`.

The floating bar runs CAF commands with checked-state feedback suppressed for that invocation, then restores the
original command setting. Otherwise a state button can interpret a plug-in's window visibility update as another
click and repeatedly toggle the window until E3D overflows its stack. Repeated clicks are ignored while a command
is queued or running. The log records the start and completion time of each invocation.

Regression checks (requires the installed E3D assemblies):

```powershell
dotnet build tests/SEP.PluginPanel.Checks -c Release
& ./tests/SEP.PluginPanel.Checks/bin/Release/net48/SEP.PluginPanel.Checks.exe
```

These checks exercise the CAF command API with simulated checked-state feedback. Opening EEXP and other plug-ins
in a fresh native E3D session remains a separate acceptance check.

*Plug-ins ▸ Plug-in panel in E3D* switches back to the classic tab (every plug-in's buttons); either change applies at
the next E3D start. Removing the last plug-in (or SEP) also removes the add-in and its `DesignAddins.xml` entry.

## E3D Live bridge

SEP has no Live page any more (removed 2026-10-12). The E3D Live bridge add-in is an ordinary plug-in,
`D:\AVEVA\Plugins\E3DLiveBridge`, enabled on the Plug-ins page like any other, so every normal E3D start loads it.
The add-in advertises each running E3D in `%LOCALAPPDATA%\E3DLiveBridge\discovery`; an agent connects through
the `e3d_live` MCP server (`python -m integrations.e3d_live.mcp_server`), which starts its own hidden gateway when
none is running. Nothing has to be opened in SEP first. Contract and runtime: the `e3d_live` repository.

Plug-in scanning never turns test projects, source build output (`obj`, `src`) or .NET Core / .NET 5+ builds into
PML.NET imports: E3D 3.1 runs .NET Framework 4.x, and one such import made every Design start exit
(`System.Runtime 10.0` not found). Add-in selection is unchanged.


## Data

Everything lives in `%APPDATA%\SEP` (or next to the exe when a `.portable` file is present); the diagnostic log is
`%LOCALAPPDATA%\SEP\sep_debug.log` (next to the exe in portable mode), rotated at 2 MB:

- `e3d_projects.json` – libraries, project cache, my projects, project details, categories, notifications, settings
- `e3d_paths.json` – E3D installation paths
- `sep_admin_creds.json` – per-project administrator credentials (DPAPI-protected)
- `sep_user_cache.json` – last user / team listing per project

Settings → *Export bundle* packs these into one file to move to another PC.

## Command-line switches

- `--install [--quiet]` / `--uninstall [--quiet]` – per-user install, update and removal (see *Install on a PC*)

- `--page=overview|projects|mine|plugins|users|tools|settings` – open a page directly
- `--apply-plugins=<request.json>` – headless plug-in setup for installers (used by the E3DHUB "Install E3D tools" download and its uninstaller); no window opens, also not on an error. The request lists `plugins` to enable and `disable` to switch off, optionally `removeRibbonIfUnused` (drop `SEP.uic` when nothing is left), `pluginsDir`, `e3dHome`, `projectsDir` (saved to SEP's settings) and `result` (where the outcome JSON goes). Every other plug-in keeps its state; enabling is exactly the Plug-ins page: `pml.index`, the `custom_evars.bat` managed block, `SEP.uic`, add-in DLLs and `DesignAddins.xml`. Exit code 0 = done, 1 = something missing, not deployed or not withdrawn, 2 = unreadable request, 3 = SEP failed. See `SEP.App/Services/PluginCli.cs`.

Uninstalling a plug-in withdraws only the add-ins SEP deployed from it (or the plug-in's own add-in assemblies of the same size in the E3D folder), never framework or AVEVA libraries: it used to delete every same-named DLL from the E3D folder, and plug-ins such as Cad2E3DAids carry `System.Memory.dll`, `System.Text.Json.dll` … that E3D ships itself.
- `--shot-dir=<folder>` – UI-check aid: drop `main.req` into the folder to get `main.png` rendered from the visual tree
