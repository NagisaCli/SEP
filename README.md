# SEP — AVEVA E3D Project Manager

Native Windows client (.NET 10, WPF, Fluent design) for managing AVEVA Everything3D projects: scan project libraries, keep project details (category / tags / owner / status / notes), pick "my projects", switch what E3D loads and launch it in one click, manage project users and teams, plug-ins, and the health of the E3D configuration.

SEP — AVEVA E3D 工程管理系统：扫描项目库、维护工程信息、一键切换并唤起 E3D、管理工程用户与团队、插件与环境诊断。

## Layout

| Path | What it is |
|---|---|
| `SEP.App/` | The WPF client (`SEP.exe`). Pages: Overview, Project Workbench, My Projects, Plug-ins, Users & Permissions, Diagnostics & Tools, Settings. |
| `ADMIN/E3dAdmin/` | Library + CLI (`e3d-admin`) that drives AVEVA `adm.exe` for user / team administration; referenced by the app. |
| `build.ps1` | `dotnet publish` single-file release into `dist\SEP.exe` and copies it to the repo root. |

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

## Data

Everything lives in `%APPDATA%\SEP` (or next to the exe when a `.portable` file is present):

- `e3d_projects.json` – libraries, project cache, my projects, project details, categories, notifications, settings
- `e3d_paths.json` – E3D installation paths
- `sep_admin_creds.json` – per-project administrator credentials (DPAPI-protected)
- `sep_user_cache.json` – last user / team listing per project

Settings → *Export bundle* packs these into one file to move to another PC.

## Command-line switches

- `--page=overview|projects|mine|plugins|users|tools|settings` – open a page directly
- `--shot-dir=<folder>` – UI-check aid: drop `main.req` into the folder to get `main.png` rendered from the visual tree
