# Engineering feature coverage — 0.4.0

The MCP server has 41 tools. Comfort/classic on V15.1 is the primary profile; Unified follows with version limits. PLC support uses the same V15.1-first offline stack. Installed V15.1 and V19 builds and offline tests do not establish native write/import/generation/compile/save/reopen acceptance; those trials remain pending on disposable projects. Exact V15.0 is not installed/tested here. The WinForms UI remains classic-only.

| Feature | Comfort / classic | Unified | Route and limit |
| --- | --- | --- | --- |
| Discover projects and HMIs | Implemented | Implemented where API exposes Unified | Exact process/project attachment; API and target capabilities |
| Browse engineering objects | Implemented | Implemented | `hmi_read_object`; API-visible descendants, no parent/services navigation |
| Scalar property edits | Where writable | Where writable | Native metadata/public properties; strings, enums, numbers, bool, color, date/time; complex unsupported types reported |
| Create objects/folders | Advertised native types | Advertised native types | Supported scalar creation arguments only |
| Delete objects | Native Delete, excluding cycles | Native Delete | Member preview and subtree baseline; roots/singletons excluded |
| Text-list translations | Specialized XML adapter | Paired-YAML adapter on matching newer API | Existing single-value entries/locales; V19 Unified has no route |
| Text-list structure/additions | Native XML import | Native YAML import where available | Generic import; native replacement scope, no specialized entry CRUD |
| Normal screens/folders | XML export/import, including contents | Native graph and creation/edit/delete | Unified serialization requires an actual native method; none synthesized |
| Captions/layout/colors | Scalar fields or screen XML | Dedicated style-preserving caption tools and generic properties | Complex text can be explicit raw markup; no automatic dynamic/multi-run rewriting |
| Comfort XML scalar editing | Dedicated read/preview/apply workflow | Not routed through classic XML | Exact XPath/old values, retained sidecars/languages, native readback equality |
| Templates/popups/slide-ins | Native XML resources | Available native screen/group/item model | Device/API dependent; unavailable categories reported |
| Permanent/global screens | Dedicated XML singleton import | Available native model | Classic ImportScreenOverview / ImportScreenGlobalElements |
| Tags/tables/groups | XML resources and advertised operations | Native objects and YAML export/import | API-dependent properties and signatures |
| Scripts | VB-script XML | Script-module YAML/JS | Retain companions; classic export uses ExportOptions.None |
| Connections | Resources and native objects | Native objects | Integrated classic connections can be unexportable |
| Cycles | Export; import missing cycles only | Available tag acquisition/configuration model | Existing classic cycles never overwritten/edited/deleted |
| Graphics / graphic lists | XML with sidecars | Shared graphics; graphic-list API where present | Graphics are project-wide; no guessed V19 Unified graphic-list API |
| Alarms/classes/logs/audit | Only if exposed by target; no dedicated classic alarm serializer | Advertised object operations | No invented categories or complex value adapters |
| Events/dynamizations | Screen XML where exported | Browse, advertised creation, scalar edits, delete | Static dynamized properties protected; explicit binding/event edits separate |
| Nested controls/parts | Screen XML where exported | Available native navigators | Complex CLR values excluded from scalar tools |
| Bulk export | API-visible resources and metadata | API-visible resources and metadata | Explicit failures, native companions; not a full backup |
| Bulk import | Dependency order, supported folders, skip existing cycles | Serializable resources and supported folders/groups | New indexed bundle format; metadata-only objects not restored |
| Compile diagnostics | Implemented | Where compile service exists | Does not save or download |
| Whole-project save | Implemented | Implemented | Exact project path; includes other editors' changes |
| Library faceplate definitions / project text XLSX | No dedicated adapter | No dedicated adapter | Outside HMI descendant/resource workflow |
| Download / running-device control | Not implemented | Not implemented | Engineering plugin scope |

PLC tools add discovery, offline object/property/group operations, block/type/tag-table native XML reads/import/export, dependency-ordered bundles, reviewed SCL/STL generation and compile diagnostics. Only a verified native Offline state permits PLC export/mutation. Supported software roots exclude hardware, technology objects, watch/force tables and online services. Protected blocks may provide only public interfaces; generic imports/generation do not assert source-content equality. Source generation keeps a new external source and may replace declared blocks; its preview makes that scope explicit.

Mutations use expiring single-attempt previews, candidate hashes, explicit target identity, native exclusive access and transactions. Scalar edits verify authored returned fields, allowing known computed PLC modification/compile metadata to update. Generic imports retain before/after evidence with content equality not asserted. Compile runs outside an edit transaction. Save and runtime behavior are separate results.

PLC references: [native blocks and languages](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-a-plc-device/blocks/exporting-blocks), [external-source generation](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/functions-for-accessing-the-data-of-a-plc-device/blocks/generating-block/udt-from-external-source-file-in-specific-user-group), [transaction restrictions](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/transaction-handling). The installed V15.1 assembly/XML documentation was also checked directly for the implemented signatures.

Siemens references: [self-description](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/general-functions/self-description-support-for-attributes-navigators-actions-and-services), [classic exportable screen objects](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/importing/exporting-data-of-an-hmi-device/screens/overview-of-exportable-screen-objects), [Unified tags/tables](https://docs.tia.siemens.cloud/r/en-us/v20/functions-for-accessing-the-data-of-an-hmi-unified-device-rt-unified/hmisoftware-rt-unified/tagtables-rt-unified/description-tagtables-rt-unified), [Unified scripts](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/global-script/description-of-global-script), [newer Unified graphic lists](https://docs.tia.siemens.cloud/r/en-us/v21/functions-for-accessing-the-data-of-an-hmi-unified-device/hmisoftware/graphic-lists/description-graphic-lists).
