# Agent Instructions & Guidelines

## Language & System Compatibility Rule (Mandatory)

> **User Directive**:
> 怎么弄着弄着成中文版了，以后只有和我的对话是中文，项目本身内容全英文，为了系统兼容；

### Strict Guidelines:
1. **User Communication (与用户对话)**:
   - All conversational responses, explanations, planning discussions, and progress reports in chat must be in **Chinese (中文)**.

2. **Project Codebase & Content (项目本身内容全英文)**:
   - **User Interface (UI)**: All GUI elements (WinForms, WPF, Qt, HTML, CLI) including window titles, buttons, labels, tooltips, menu items, context menus, and status bars must strictly use **English**.
   - **Dialogs & Prompts**: All message boxes, input prompts, warnings, error messages, confirmation popups, and toast notifications must strictly use **English**.
   - **Code, Comments & Logs**: All source code, docstrings, comments, log output, exception messages, and console prints must strictly use **English**.
   - **PML, Schemas & Configs**: All AVEVA PML files, AutoCAD commands, XML/JSON manifests, and configuration files must strictly use **English**.
   - **Compatibility Rationale**: Engineering CAD and plant software (such as AutoCAD, AVEVA E3D/PDMS, PML, DABACON) require strict encoding, font rendering, and locale stability. Pure English project content eliminates mojibake (乱码), font fallback failures, and international system compatibility issues.
