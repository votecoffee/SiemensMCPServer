---
name: inspect-project-texts
description: Export and inspect TIA Portal project-text XLSX workbooks through SiemensMCPServer for Comfort/classic or other API-supported engineering texts. Use for active-language discovery and text search; excludes workbook edits and translation import.
---

Use `tia_list_extensions` to inspect the project module's schemas. These operations require host 0.9.0 or newer; `tia_get_api_capabilities().projectTexts` distinguishes member presence from native acceptance and reports import as unimplemented.

Attach with the exact process/project path and use the attached path from `tia_get_status`. Call `tia_read_extension` with `module="project"`, `operation="list_project_languages"` and arguments containing `expectedProjectPath`, `capture={}` and `options={}`. Select two distinct already-active culture names from the result; do not activate languages or choose an arbitrary first language.

For `read_project_texts`, supply the same project path and `capture={"sourceLanguage":"<active culture>","targetLanguage":"<different active culture>"}`. Use `options.sheet`, `offset`, `maxCells` and `maxCharacters` for the bounded cell view. For `find_in_project_texts`, use the same capture with literal `query`, explicit `caseSensitive` and optional `sheet`/`maxMatches`.

Retain `data.evidence.workbook` and its artifact directory, filename and SHA-256. Every read/search call exports a fresh workbook; paging is not a fixed snapshot. Honor continuation and clipping flags. Cells include headers/native identifiers and are not classified as safe translation targets. Formula values are cached, styles are not rendered, and formulas/external links are not evaluated.

This workflow preserves the native XLSX and performs no workbook editing, language activation or native text import. Do not route a workbook through HMI/PLC resource import tools. Native read/export, API metadata checks and runtime acceptance are distinct. Read the [project-text guide](../../PROJECT-TEXTS.md) for limits and originating-project restrictions.
