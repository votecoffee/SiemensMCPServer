---
name: inspect-project-texts
description: Extract and update existing multilingual TIA project comments, descriptions, captions and text values through native project-text workbooks. Use for an attached project or retained export; excludes language activation and runtime control.
---

Use `tia_list_extensions` to inspect the project module's schemas. Translation previews/import require host 0.12.0 or newer; older hosts reject the new project worker and retain the previous generation. `tia_get_api_capabilities().projectTexts` distinguishes API member presence and implementation from native acceptance.

Attach with the exact process/project path and use the attached path from `tia_get_status`. Call `tia_read_extension` with `module="project"`, `operation="list_project_languages"` and arguments containing `expectedProjectPath`, `capture={}` and `options={}`. Select two distinct already-active culture names from the result; do not activate languages or choose an arbitrary first language.

For `read_project_texts`, supply the same project path and `capture={"sourceLanguage":"<active culture>","targetLanguage":"<different active culture>"}`. Use `options.sheet`, `offset`, `maxCells` and `maxCharacters` for the bounded cell view. For `find_in_project_texts`, use the same capture with literal `query`, explicit `caseSensitive` and optional `sheet`/`maxMatches`.

Retain `data.evidence.workbook` and its artifact directory, filename and SHA-256. Every read/search call exports a fresh workbook; paging is not a fixed snapshot. Honor continuation and clipping flags. Cells include headers/native identifiers and are not classified as safe translation targets. Formula values are cached, styles are not rendered, and formulas/external links are not evaluated.

For comments, descriptions, captions and translations, use `read_translation_units` with the same capture and bounded `options` (`sheet`, `query`, exact `category`, `missingOnly`, `offset`, `maxUnits`, `maxCharacters`). Retain `workbookFingerprint` and copy returned target/source selections. Clipped fields, system text, complex markup and substitutions are inspection-only. Empty targets can be intentional; do not invent translations without the user's intended content or languages.

Preview existing values through `tia_preview_extension`, `module=project`, `operation=edit_project_texts`, `targetId=project`, empty `path`, and `expectedFingerprint=workbookFingerprint`. Arguments require the exact project path, language capture and `edits=[{selection:<returned selection>,newText:<reviewed value>}]`. Source selections require explicit `updateSourceLanguage=true`; target edits default to false. Inspect retained before/candidate workbooks and the old/new values before applying an authorized matching plan/hash through `tia_apply_extension_plan`. Apply checks fresh full text readback in a native transaction and leaves the project unsaved; compile affected targets and save separately within the user's scope. On uncertain failure, inspect the project before retrying a new preview.

The companion `Edit-ProjectTexts.ps1` extracts complete retained workbooks or prepares disk-only candidates using the same engine; it does not import them. Use new output directories and preserve native IDs/metadata. Keep customer workbooks and reports outside the public tree or under ignored `.local/`.

Do not route a workbook through HMI/PLC resource import tools or add project language slots through this workflow. Native extraction, import/readback, rendering and runtime acceptance are distinct. Read the [project-text guide](../../PROJECT-TEXTS.md) for exact schemas, bounds and originating-project restrictions. Native import acceptance remains pending.
