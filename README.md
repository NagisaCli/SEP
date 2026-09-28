# SEP — AVEVA E3D Project Manager

Native Windows client (.NET 10, WPF, Fluent design) for managing AVEVA Everything3D projects: scan project libraries, keep project details (category / tags / owner / status / notes), pick "my projects", switch what E3D loads and launch it in one click, manage project users and teams, plug-ins, and the health of the E3D configuration.

SEP — AVEVA E3D 工程管理系统：扫描项目库、维护工程信息、一键切换并唤起 E3D、管理工程用户与团队、插件与环境诊断。

## Layout

| Path | What it is |
|---|---|
| `SEP.App/` | The WPF client (`SEP.exe`). Pages: Overview, Project Workbench, My Projects, Plug-ins, Users & Permissions, Diagnostics & Tools, Settings. |
| `ADMIN/E3dAdmin/` | Library + CLI (`e3d-admin`) that drives AVEVA `adm.exe` for user / team administration; referenced by the app. |
| `build.ps1` | Self-contained single-file release `dist\SEP.exe`, plus `dist\Install-SEP.cmd` and `dist\SEP-<version>-setup.zip`; copies the exe to the repo root. |
| `SEP.App/Services/LiveEntryAdapter.cs` | Optional, isolated adapter to the separate `e3d_live` entry API. |

## Build

Requires the .NET 10 SDK.

```powershell
.\build.ps1
```

The default build is **self-contained**: the .NET runtime travels inside `SEP.exe` (about 65 MB), so it starts on
any 64-bit Windows 10 or 11 PC without installing anything first. `.\build.ps1 -FrameworkDependent` makes a small
exe (about 8 MB) that needs the .NET 10 Desktop Runtime on every PC. `dotnet build SEP.App\SEP.App.csproj -c Release`
is the quick developer build.

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

## Optional Live launch

The **Live launch** page is separate from every existing SEP launch action. It
reads target choices from `python -m integrations.e3d_live.live_entry options`
in the selected `e3d_live` checkout. The Live repository remains responsible
for its module profiles, runtimes, E3D process selection, gateway,
session, permissions, and verification. SEP stores only the checkout path,
Python executable, and per-primary support-project selections in
`live_entry.json` under its normal data directory. It
does not copy or compile Live source into SEP.

SEP lists any discovered project with a valid AVEVA code. The page sends the
chosen primary evars path plus the optional support project selected for
cross-project MDB dependencies. Live creates private launch/evars copies for
the new E3D process and masks support projects in the normal login list. It
does not edit SEP's ordinary E3D launch files or change the remembered launch
mode. AVEVA's Show Masked Projects option can still reveal support projects;
native MDB lists and later module switching are not blocked by this UI mask.
SEP reads the selected project's MDB names through a read-only E3D Admin query
and offers them in the MDB dropdown. If Admin cannot return a list, the field
remains editable so a known MDB can still be entered. SEP passes the chosen
evars path, code, selected MDB, module and access mode to
the Live CLI. Live verifies the evars code before launch;
its module profile decides whether the selected runtime/mode is supported.
The ordinary
project-card, title-bar, library and batch launch buttons are unchanged.

SEP does not present an unverified free-text project as launchable. An MDB may
also be entered manually but must actually exist in E3D; login remains interactive. A `ready`
message confirms exact Live session and
gateway binding, not an in-panel write grant or saved E3D work. See the
separate `e3d_live/integrations/e3d_live/docs/live-entry-api.md` for the CLI
contract and runtime registry.

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
- `--page=live` – open the optional Live launch page
- `--apply-plugins=<request.json>` – headless plug-in setup for installers (used by the E3DHUB "Install E3D tools" download and its uninstaller); no window opens, also not on an error. The request lists `plugins` to enable and `disable` to switch off, optionally `removeRibbonIfUnused` (drop `SEP.uic` when nothing is left), `pluginsDir`, `e3dHome`, `projectsDir` (saved to SEP's settings) and `result` (where the outcome JSON goes). Every other plug-in keeps its state; enabling is exactly the Plug-ins page: `pml.index`, the `custom_evars.bat` managed block, `SEP.uic`, add-in DLLs and `DesignAddins.xml`. Exit code 0 = done, 1 = something missing, not deployed or not withdrawn, 2 = unreadable request, 3 = SEP failed. See `SEP.App/Services/PluginCli.cs`.

Uninstalling a plug-in withdraws only the add-ins SEP deployed from it (or the plug-in's own add-in assemblies of the same size in the E3D folder), never framework or AVEVA libraries: it used to delete every same-named DLL from the E3D folder, and plug-ins such as Cad2E3DAids carry `System.Memory.dll`, `System.Text.Json.dll` … that E3D ships itself.
- The Live page lists health-verified E3D Live sessions and can gracefully stop an exact gateway binding. Stop Live leaves E3D open so its normal Save Work and close flow remains available. Separate project/MDB/module scopes can run concurrently on distinct gateway ports; a pending native login is serialized.
- `--shot-dir=<folder>` – UI-check aid: drop `main.req` into the folder to get `main.png` rendered from the visual tree
