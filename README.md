# SEP — AVEVA E3D Project Manager

Native Windows client (.NET 10, WPF, Fluent design) for managing AVEVA Everything3D projects: scan project libraries, keep project details (category / tags / owner / status / notes), pick "my projects", switch what E3D loads and launch it in one click, manage project users and teams, plug-ins, and the health of the E3D configuration.

SEP — AVEVA E3D 工程管理系统：扫描项目库、维护工程信息、一键切换并唤起 E3D、管理工程用户与团队、插件与环境诊断。

## Layout

| Path | What it is |
|---|---|
| `SEP.App/` | The WPF client (`SEP.exe`). Pages: Overview, Project Workbench, My Projects, Plug-ins, Users & Permissions, Diagnostics & Tools, Settings. |
| `ADMIN/E3dAdmin/` | Library + CLI (`e3d-admin`) that drives AVEVA `adm.exe` for user / team administration; referenced by the app. |
| `build.ps1` | `dotnet publish` single-file release into `dist\SEP.exe` and copies it to the repo root. |
| `SEP.App/Services/LiveEntryAdapter.cs` | Optional, isolated adapter to the separate `e3d_live` entry API. |

## Build

Requires the .NET 10 SDK.

```powershell
.\build.ps1
```

or `dotnet build SEP.App\SEP.App.csproj -c Release` for a quick build.

## How it launches E3D

- **Single project** – the project's `evarsXXX.bat` is written into the `SEP MANAGED PROJECTS` block at the end of the local library's `custom_evars.bat`, then E3D is started.
- **Whole library** – `projects_dir=` in `evars.bat` / `evars.init` is pointed at the library root.
- **All my projects** – every project in *My Projects* is written into the managed block, so all of them appear in the E3D login window.

A folder is a project when it contains `evarsXXX.bat`; a folder whose sub-folders contain such files is a project library. Local paths and `\\server\share` are supported.

## Optional Live launch

The **Live launch** page is separate from every existing SEP launch action. It
reads target choices from `python -m integrations.e3d_live.live_entry options`
in the selected `e3d_live` checkout. The Live repository remains responsible
for its module profiles, runtimes, E3D process selection, gateway,
session, permissions, and verification. SEP stores only the checkout path and
Python executable in `live_entry.json` under its normal data directory. It
does not copy or compile Live source into SEP.

SEP lists any discovered project with a valid AVEVA code. The page calls SEP's
existing `SwitchAsync` to prepare that exact project's evars (without the
ordinary E3D launch), then passes its evars path, code, the entered MDB, module
and access mode to the Live CLI. Live verifies the evars code before launch;
its module profile decides whether the selected runtime/mode is supported.
The ordinary
project-card, title-bar, library and batch launch buttons are unchanged.

SEP does not present an unverified free-text project as launchable. An MDB may
be entered but must actually exist in E3D; login remains interactive. A `ready`
message confirms exact Live session and
gateway binding, not an in-panel write grant or saved E3D work. See the
separate `e3d_live/integrations/e3d_live/docs/live-entry-api.md` for the CLI
contract and runtime registry.

## Data

Everything lives in `%APPDATA%\SEP` (or next to the exe when a `.portable` file is present):

- `e3d_projects.json` – libraries, project cache, my projects, project details, categories, notifications, settings
- `e3d_paths.json` – E3D installation paths
- `sep_admin_creds.json` – per-project administrator credentials (DPAPI-protected)
- `sep_user_cache.json` – last user / team listing per project

Settings → *Export bundle* packs these into one file to move to another PC.

## Command-line switches

- `--page=overview|projects|mine|plugins|users|tools|settings` – open a page directly
- `--page=live` – open the optional Live launch page
- The Live page lists health-verified E3D Live sessions and can gracefully stop an exact gateway binding. Stop Live leaves E3D open so its normal Save Work and close flow remains available. Separate project/MDB/module scopes can run concurrently on distinct gateway ports; a pending native login is serialized.
- `--shot-dir=<folder>` – UI-check aid: drop `main.req` into the folder to get `main.png` rendered from the visual tree
