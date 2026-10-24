using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SiemensMCPServer.Extensions;
using SiemensMCPServer.Mcp;

namespace SiemensMCPServer.Analysis
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ScriptCoverage
    {
        public string HmiId { get; set; } = "";
        [JsonRequired] public string Category { get; set; } = "";
        [JsonRequired] public string Status { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ScriptSchedulerEvidence
    {
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public string Kind { get; set; } = "";
        [JsonRequired] public string Content { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class ScriptAnalysisRequest
    {
        [JsonRequired] public HmiResourceInput[] Resources { get; set; } = Array.Empty<HmiResourceInput>();
        public ScriptCoverage[] Coverage { get; set; } = Array.Empty<ScriptCoverage>();
        public ScriptSchedulerEvidence[] SchedulerEvidence { get; set; } = Array.Empty<ScriptSchedulerEvidence>();
        public string View { get; set; } = "registrations";
        public int Offset { get; set; }
        public int MaxItems { get; set; } = 100;
    }

    // Static literal-pattern evidence, never a VBScript evaluator or scheduler model.
    internal static class HmiScriptAnalysis
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        private static readonly string[] Categories = { "VBScripts", "TagTables", "Screens", "PermanentArea", "ScreenTemplates", "PopupScreens", "SlideInScreens", "Cycles", "ScheduledTasks" };
        private static readonly string[] Views = { "scripts", "registrations", "calls", "dependencies", "diagnostics" };
        private static readonly HashSet<string> Builtins = new HashSet<string>(("Abs Array Asc AscB AscW Atn CBool CByte CCur CDate CDbl Chr ChrB ChrW CInt CLng Cos CSng CStr Date DateAdd DateDiff DatePart DateSerial DateValue Day Escape Eval Exp Filter Fix FormatCurrency FormatDateTime FormatNumber FormatPercent GetLocale GetObject GetRef Hex Hour InStr InStrB InStrRev Int IsArray IsDate IsEmpty IsNull IsNumeric IsObject Join LBound LCase Left LeftB Len LenB LoadPicture Log LTrim Mid MidB Minute Month MonthName MsgBox Now Oct Replace RGB Right RightB Rnd Round RTrim ScriptEngine Second SetLocale Sgn Sin Space Split Sqr StrComp String StrReverse Time Timer TimeSerial TimeValue Trim TypeName UBound UCase Unescape VarType Weekday WeekdayName Year SmartTags Tags Read Write").Split(' '), Names);
        private sealed class Source
        {
            public HmiResourceInput Input = null!;
            public XElement Root = null!;
            public Dictionary<XElement, string> Paths = new Dictionary<XElement, string>();
            public Dictionary<string, XElement> Ids = new Dictionary<string, XElement>(StringComparer.Ordinal);
            public string Category = "", Sha256 = "";
        }
        private sealed class Script
        {
            public Source Source = null!;
            public string Id = "", Name = "", CodePath = "", Code = "";
            public bool Opaque, Loops, Conversions, ResumeNext;
            public int UiWrites;
            public List<object> ClearEvidence = new List<object>();
            public List<Dependency> Dependencies = new List<Dependency>();
            public List<Call> Calls = new List<Call>();
        }
        private sealed class Dependency
        {
            public string ScriptId { get; set; } = "";
            public string Tag { get; set; } = "";
            public string Access { get; set; } = "";
            public int Line { get; set; }
        }
        private sealed class Call
        {
            public string ScriptId { get; set; } = "";
            public string Target { get; set; } = "";
            public string Status { get; set; } = "unresolved";
            public string TargetScriptId { get; set; } = "";
            public int Line { get; set; }
        }
        private sealed class Registration
        {
            public string HmiId { get; set; } = "";
            public string ResourcePath { get; set; } = "";
            public string XPath { get; set; } = "";
            public string SourceSha256 { get; set; } = "";
            public string Event { get; set; } = "";
            public string EventKind { get; set; } = "";
            public bool IsEventRegistration { get; set; }
            public string Script { get; set; } = "";
            public string TargetScriptId { get; set; } = "";
            public string Resolution { get; set; } = "";
            public string ReferenceKind { get; set; } = "native-script-link";
            public string TriggerTag { get; set; } = "";
            public Dictionary<string, string> AcquisitionFields { get; set; } = new Dictionary<string, string>();
        }
        private sealed class Token
        {
            public string Value = "";
            public bool Literal;
            public int Line;
        }
        public static object Schema() => new { type = "object", additionalProperties = false, required = new[] { "resources" }, properties = new {
            resources = new { type = "array", minItems = 1, maxItems = 1000, items = new { type = "object", additionalProperties = false, required = new[] { "path", "xml" }, properties = new {
                hmiId = new { type = "string", maxLength = 4096 }, path = new { type = "string", maxLength = 4096 }, xml = new { type = "string", maxLength = 2097152 } } } },
            coverage = new { type = "array", maxItems = 1000, items = new { type = "object", additionalProperties = false, required = new[] { "category", "status" }, properties = new {
                hmiId = new { type = "string", maxLength = 4096 }, category = new { type = "string", @enum = Categories }, status = new { type = "string", @enum = new[] { "complete", "partial", "notSupplied", "unsupported" } } } } },
            schedulerEvidence = new { type = "array", maxItems = 20, items = new { type = "object", additionalProperties = false, required = new[] { "id", "kind", "content" }, properties = new {
                id = new { type = "string", maxLength = 256 }, kind = new { type = "string", @enum = new[] { "scheduler-configuration", "runtime-observation", "deployed-image" } }, content = new { type = "string", maxLength = 65536 } } } },
            view = new { type = "string", @enum = Views, @default = "registrations" }, offset = new { type = "integer", minimum = 0, maximum = 1000000, @default = 0 },
            maxItems = new { type = "integer", minimum = 1, maximum = 200, @default = 100 }
        } };
        private static string Scalar(XElement? node, string field) => HmiReferenceAnalyzer.Scalar(node, field);
        private static string Name(XElement node) => ClassicXmlDocument.ObjectNameField(node)?.Value ?? "";
        private static void Length(string? text, int max) { if (text == null || text.Length > max) throw new ArgumentException("Script analysis field exceeds its described limit."); }
        private static Source Parse(HmiResourceInput input, Action<int> charge)
        {
            if (input == null) throw new ArgumentException("A resource cannot be null.");
            Length(input.HmiId, 4096); Length(input.Path, 4096); Length(input.Xml, 2097152);
            var doc = TransferFiles.Xml(input.Xml);
            var roots = doc.Root?.Elements().Where(e => e.Name.NamespaceName == "" && e.Name.LocalName.StartsWith("Hmi.", StringComparison.Ordinal)).ToArray();
            if (doc.Root?.Name != "Document" || roots?.Length != 1) throw new ArgumentException("Supply one native classic Hmi resource per Document.");
            var source = new Source { Input = input, Root = roots[0], Sha256 = TextListDocument.Hash(input.Xml) };
            string prefix = input.Path.Split('/')[0];
            source.Category = Categories.Contains(prefix, StringComparer.Ordinal) ? prefix : roots[0].Name == "Hmi.RuntimeScripting.VBScript" ? "VBScripts" : roots[0].Name == "Hmi.Tag.TagTable" ? "TagTables" : "unknown";
            int visits = 0;
            void Visit(XElement node, string path, int depth) {
                if (++visits > 20000 || depth > 64) throw new ArgumentException("Script resource exceeds 20000 elements or depth 64.");
                charge(path.Length);
                source.Paths.Add(node, path);
                if (node.Attribute("ID") is XAttribute id) {
                    if (source.Ids.ContainsKey(id.Value)) throw new ArgumentException("Duplicate native XML ID.");
                    source.Ids.Add(id.Value, node);
                }
                var counts = new Dictionary<XName, int>();
                foreach (var child in node.Elements()) {
                    counts.TryGetValue(child.Name, out int index); counts[child.Name] = ++index;
                    Visit(child, path + "/" + child.Name.LocalName + "[" + index + "]", depth + 1);
                }
            }
            Visit(doc.Root, "/Document[1]", 0);
            return source;
        }

        // Strings (including doubled quotes), apostrophe/Rem comments, colon
        // statements and line continuations are tokenized without running code.
        private static List<List<Token>> Lex(string code)
        {
            var statements = new List<List<Token>>(); var current = new List<Token>(); int line = 1, count = 0;
            void End() { if (current.Count != 0) { statements.Add(current); current = new List<Token>(); } }
            for (int i = 0; i < code.Length;) {
                char c = code[i];
                if (c == '\r') { i++; continue; }
                if (c == '\n') { if (current.LastOrDefault()?.Value == "_") current.RemoveAt(current.Count - 1); else End(); line++; i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '\'') { while (i < code.Length && code[i] != '\n') i++; continue; }
                if (c == ':') { End(); i++; continue; }
                var token = new Token { Line = line };
                if (c == '"') {
                    token.Literal = true; i++; var text = new System.Text.StringBuilder(); bool closed = false;
                    while (i < code.Length) {
                        if (code[i] == '"') { if (i + 1 < code.Length && code[i + 1] == '"') { text.Append('"'); i += 2; continue; } i++; closed = true; break; }
                        if (code[i] == '\n') throw new ArgumentException("Unterminated VBScript string; source is inspection-only and must tokenize safely.");
                        text.Append(code[i++]);
                    }
                    if (!closed) throw new ArgumentException("Unterminated VBScript string.");
                    token.Value = text.ToString();
                } else if (char.IsLetterOrDigit(c) || c == '_') {
                    int start = i++; while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_')) i++;
                    token.Value = code.Substring(start, i - start);
                    if (current.Count == 0 && Names.Equals(token.Value, "Rem")) { while (i < code.Length && code[i] != '\n') i++; continue; }
                } else { token.Value = c.ToString(); i++; }
                if (++count > 50000) throw new ArgumentException("Script exceeds 50000 lexical tokens.");
                current.Add(token);
            }
            End(); return statements;
        }
        private static void ReadScript(Script script, HashSet<string> scriptNames)
        {
            var statements = Lex(script.Code);
            var shadowed = new HashSet<string>(Names);
            foreach (var tokens in statements) {
                if (Names.Equals(tokens[0].Value, "Dim")) foreach (var t in tokens.Skip(1).Where(t => !t.Literal && char.IsLetter(t.Value[0]))) shadowed.Add(t.Value);
                if (new[] { "Sub", "Function" }.Contains(tokens[0].Value, Names)) foreach (var t in tokens.Skip(3).Where(t => !t.Literal && char.IsLetter(t.Value[0]) && !new[] { "ByVal", "ByRef" }.Contains(t.Value, Names))) shadowed.Add(t.Value);
            }
            foreach (var tokens in statements) {
                bool Is(int i, string value) => i < tokens.Count && !tokens[i].Literal && Names.Equals(tokens[i].Value, value);
                bool AssignmentHead(int end) {
                    int start = 0;
                    for (int n = 0; n < end; n++) if (Is(n, "Then") || Is(n, "Else")) start = n + 1;
                    return start < end && !new[] { "If", "ElseIf", "While", "Until", "Select", "Case" }.Contains(tokens[start].Value, Names)
                        && !tokens.Skip(start).Take(end - start).Any(t => !t.Literal && t.Value == "=");
                }
                string first = tokens[0].Value;
                if (new[] { "For", "Do", "While" }.Contains(first, Names)) script.Loops = true;
                if (tokens.Count >= 4 && Is(0, "On") && Is(1, "Error") && Is(2, "Resume") && Is(3, "Next")) script.ResumeNext = true;
                for (int i = 0; i < tokens.Count; i++) {
                    var token = tokens[i]; if (token.Literal) continue;
                    if (new[] { "Eval", "Execute", "ExecuteGlobal", "CallByName" }.Contains(token.Value, Names)) script.Opaque = true;
                    if (new[] { "CLng", "CInt", "CDbl", "CBool", "CSng", "CDate" }.Contains(token.Value, Names) && Is(i + 1, "(")) script.Conversions = true;
                    bool smart = Names.Equals(token.Value, "SmartTags"), tagObject = Is(i, "HMIRuntime") && Is(i + 1, ".") && Is(i + 2, "Tags");
                    if (smart || tagObject) {
                        int open = i + (smart ? 1 : 3);
                        if (!Is(open, "(") || open + 2 >= tokens.Count || !tokens[open + 1].Literal || !Is(open + 2, ")")) { script.Opaque = true; continue; }
                        string tag = tokens[open + 1].Value; Length(tag, 4096);
                        int next = open + 3;
                        bool statementTarget = i == 0 || i > 0 && (Is(i - 1, "Then") || Is(i - 1, "Else"));
                        string access = smart ? (statementTarget && Is(next, "=") ? "write" : "read") : Is(next, ".") && Is(next + 1, "Read") ? "read" : Is(next, ".") && Is(next + 1, "Write") ? "write" : "handle-unknown";
                        if (access == "handle-unknown") script.Opaque = true;
                        script.Dependencies.Add(new Dependency { ScriptId = script.Id, Tag = tag, Access = access, Line = token.Line });
                        int value = smart ? next + 1 : next + 2;
                        if (access == "write" && value < tokens.Count && (value + 1 == tokens.Count || Is(value + 1, "Else")) && (tokens[value].Literal && tokens[value].Value == "" || !tokens[value].Literal && tokens[value].Value == "0"))
                            script.ClearEvidence.Add(new { kind = tokens[value].Literal ? "empty-string-write-site" : "zero-write-site", tag, line = token.Line });
                    }
                    if (AssignmentHead(i) && Is(i, ".") && (Is(i + 1, "Visible") || Is(i + 1, "Text") || Is(i + 1, "Caption")) && Is(i + 2, "=")) {
                        script.UiWrites++;
                        if (Is(i + 1, "Visible") && Is(i + 3, "False") && (i + 4 == tokens.Count || Is(i + 4, "Else"))) script.ClearEvidence.Add(new { kind = "visibility-false-write-site", line = token.Line });
                    }
                    bool declaration = i > 0 && new[] { "Sub", "Function" }.Contains(tokens[i - 1].Value, Names);
                    bool explicitCall = i > 0 && Is(i - 1, "Call");
                    bool knownCall = scriptNames.Contains(token.Value) && (Is(i + 1, "(") || i == 0 && !Is(1, "="));
                    bool unknownFunction = Is(i + 1, "(") && char.IsLetter(token.Value[0]) && !Builtins.Contains(token.Value) && !new[] { "If", "While", "ReDim" }.Contains(token.Value, Names);
                    if (!declaration && (explicitCall || knownCall || unknownFunction) && token.Value.Length <= 256) {
                        script.Calls.Add(new Call { ScriptId = script.Id, Target = token.Value, Line = token.Line, Status = shadowed.Contains(token.Value) ? "local-or-shadowed" : i > 0 && Is(i - 1, ".") ? "external-member-unknown" : "unresolved" });
                    }
                }
            }
        }
        public static JsonElement Execute(JsonElement args)
        {
            var request = ExtensionWire.Parse<ScriptAnalysisRequest>(args);
            if (request.Resources == null || request.Resources.Length < 1 || request.Resources.Length > 1000 || request.Coverage == null || request.Coverage.Length > 1000 || request.SchedulerEvidence == null || request.SchedulerEvidence.Length > 20 || !Views.Contains(request.View))
                throw new ArgumentException("Invalid script analysis resources, coverage, evidence or view.");
            if (request.Offset < 0 || request.Offset > 1000000 || request.MaxItems < 1 || request.MaxItems > 200) throw new ArgumentException("offset must be 0..1000000 and maxItems 1..200.");
            if (request.Resources.Sum(r => (long)(r?.Xml?.Length ?? 0)) > 8 * 1024 * 1024) throw new ArgumentException("Script resource aggregate exceeds 8 MiB; the direct offline profile retains its lower 4 MiB input limit.");
            foreach (var coverage in request.Coverage) {
                if (coverage == null || !Categories.Contains(coverage.Category) || !new[] { "complete", "partial", "notSupplied", "unsupported" }.Contains(coverage.Status)) throw new ArgumentException("Invalid reported category coverage.");
                Length(coverage.HmiId, 4096);
            }
            if (request.Coverage.GroupBy(c => c.HmiId + "\n" + c.Category, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new ArgumentException("Duplicate reported category coverage.");
            foreach (var evidence in request.SchedulerEvidence) {
                if (evidence == null || !new[] { "scheduler-configuration", "runtime-observation", "deployed-image" }.Contains(evidence.Kind)) throw new ArgumentException("Invalid scheduler evidence kind.");
                Length(evidence.Id, 256); Length(evidence.Content, 65536);
            }
            if (request.SchedulerEvidence.GroupBy(e => e.Id, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new ArgumentException("Duplicate scheduler evidence IDs.");
            int elements = 0; long pathCharacters = 0;
            void Charge(int length) {
                if (++elements > 200000 || (pathCharacters += length) > 8 * 1024 * 1024)
                    throw new ArgumentException("Script analysis exceeds 200000 elements or 8 MiB of XPath locator characters.");
            }
            var sources = request.Resources.OrderBy(r => r?.HmiId, StringComparer.Ordinal).ThenBy(r => r?.Path, StringComparer.Ordinal).Select(r => Parse(r, Charge)).ToArray();
            if (sources.GroupBy(s => s.Input.HmiId + "\n" + s.Input.Path, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new ArgumentException("Resource identity limit exceeded.");
            var scripts = new List<Script>(); var diagnostics = new List<object>();
            void Issue(string code, string script, string message, object? evidence = null) => diagnostics.Add(new { code, scriptId = script, message, evidence });
            foreach (var source in sources.Where(s => s.Root.Name == "Hmi.RuntimeScripting.VBScript")) {
                if (scripts.Count >= 256) throw new ArgumentException("At most 256 scripts can be inspected.");
                var code = source.Root.Element("AttributeList")?.Elements().Where(e => new[] { "Code", "SourceCode" }.Contains(e.Name.LocalName)).ToArray() ?? Array.Empty<XElement>();
                var script = new Script { Source = source, Name = Name(source.Root), Id = TextListDocument.Hash(source.Input.HmiId + "\n" + source.Input.Path), Opaque = code.Length != 1 || code.Any(e => e.HasElements) };
                Length(script.Name, 256);
                if (script.Name.Length == 0) throw new ArgumentException("An exported VBScript must have one scalar native name.");
                if (!script.Opaque) { script.Code = code[0].Value; script.CodePath = source.Paths[code[0]]; Length(script.Code, 262144); }
                else Issue("opaque-script-source", script.Id, "Expected one scalar Code/SourceCode field. Missing/structured code remains unknown.");
                scripts.Add(script);
            }
            var scriptNames = new HashSet<string>(scripts.Select(s => s.Name), Names);
            foreach (var script in scripts) {
                ReadScript(script, scriptNames);
                if (scripts.Sum(s => s.Calls.Count + s.Dependencies.Count) > 20000) throw new ArgumentException("Script call/dependency site limit exceeded (20000).");
            }
            Script[] Resolve(string hmi, string name) => scripts.Where(s => Names.Equals(s.Source.Input.HmiId, hmi) && Names.Equals(s.Name, HmiReferenceAnalyzer.Unquote(name))).ToArray();
            foreach (var script in scripts) foreach (var call in script.Calls) {
                if (call.Status == "local-or-shadowed" || call.Status == "external-member-unknown") continue;
                var matches = Resolve(script.Source.Input.HmiId, call.Target);
                call.Status = matches.Length == 1 ? "resolved-exported-script-candidate" : matches.Length == 0 ? "not-exported" : "ambiguous";
                if (matches.Length == 1) call.TargetScriptId = matches[0].Id;
            }
            var registrations = new List<Registration>();
            foreach (var source in sources) foreach (var link in source.Paths.Keys.Where(e =>
                e.Parent?.Name == "LinkList" && (new[] { "Script", "VBScript" }.Contains(e.Name.LocalName) || e.Name == "Function" && scriptNames.Contains(HmiReferenceAnalyzer.Unquote(Scalar(e, "Name")))) ||
                e.Name == "Name" && e.Parent?.Name == "AttributeList" && e.Parent.Parent?.Name.LocalName == "Hmi.Event.FunctionListEntry" && scriptNames.Contains(HmiReferenceAnalyzer.Unquote(e.Value)) &&
                    !e.Parent.Parent.Descendants("LinkList").Elements().Any(l => new[] { "Script", "VBScript" }.Contains(l.Name.LocalName) || l.Name == "Function" && scriptNames.Contains(HmiReferenceAnalyzer.Unquote(Scalar(l, "Name")))))) {
                if (registrations.Count >= 10000) throw new ArgumentException("At most 10000 script registrations can be inspected.");
                var eventNode = link.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Hmi.Event.Event");
                var tag = link.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Hmi.Tag.Tag");
                bool candidate = link.Name == "Name" || link.Name == "Function";
                string target = link.Name == "Name" ? link.Value : Scalar(link, "Name");
                string id = (string?)link.Attribute("TargetID") ?? "";
                if (link.Name != "Name" && id != "@OpenLink") target = source.Ids.TryGetValue(NativeXmlIds.TargetKey(id), out var local) && local == source.Root && local.Name == "Hmi.RuntimeScripting.VBScript" ? Name(local) : "";
                var candidates = Resolve(source.Input.HmiId, target);
                var acquisition = new Dictionary<string, string>();
                if (tag != null) foreach (var field in tag.Elements().Where(e => e.Name == "AttributeList" || e.Name == "LinkList").Elements().Where(e => e.Name.LocalName.IndexOf("Acquisition", StringComparison.OrdinalIgnoreCase) >= 0 || e.Name.LocalName.IndexOf("Cycle", StringComparison.OrdinalIgnoreCase) >= 0).Take(20)) {
                    string value = field.HasElements ? Scalar(field, "Name") : field.Value; Length(value, 8192); acquisition[source.Paths[field]] = value;
                }
                string eventName = eventNode == null ? "" : Name(eventNode);
                registrations.Add(new Registration { HmiId = source.Input.HmiId, ResourcePath = source.Input.Path, XPath = source.Paths[link], SourceSha256 = source.Sha256,
                    IsEventRegistration = eventNode != null, Event = eventName, EventKind = Names.Equals(eventName, "ChangeValue") ? "tag-change-edge" : new[] { "Loaded", "OnLoaded", "ScreenLoaded", "OnOpen" }.Contains(eventName, Names) ? "screen-entry-candidate" : source.Category == "ScheduledTasks" && new[] { "Cyclic", "Cycle", "Timer" }.Contains(eventName, Names) ? "periodic-configuration-candidate" : "unknown",
                    Script = HmiReferenceAnalyzer.Unquote(target), ReferenceKind = candidate ? "exported-name-candidate" : "native-script-link", Resolution = candidates.Length == 1 ? candidate ? "exported-name-candidate" : "resolved-exported-script" : candidates.Length == 0 ? "not-exported" : "ambiguous",
                    TargetScriptId = candidates.Length == 1 ? candidates[0].Id : "", TriggerTag = tag == null ? "" : Name(tag), AcquisitionFields = acquisition });
            }
            var byId = scripts.ToDictionary(s => s.Id, StringComparer.Ordinal);
            Script[] Reach(Script start) {
                var seen = new HashSet<string>(StringComparer.Ordinal); var queue = new Queue<Script>(); queue.Enqueue(start);
                while (queue.Count != 0) { var current = queue.Dequeue(); if (!seen.Add(current.Id)) continue; foreach (var call in current.Calls.Where(c => c.TargetScriptId.Length != 0)) queue.Enqueue(byId[call.TargetScriptId]); }
                return seen.Select(id => byId[id]).ToArray();
            }
            var closures = scripts.ToDictionary(s => s.Id, Reach, StringComparer.Ordinal);
            var periodicallyReachable = new HashSet<string>(registrations.Where(r => r.EventKind == "periodic-configuration-candidate" && r.TargetScriptId.Length != 0)
                .SelectMany(r => closures[r.TargetScriptId]).Select(s => s.Id), StringComparer.Ordinal);
            foreach (var script in scripts) {
                var reachable = closures[script.Id]; var handlers = registrations.Where(r => r.IsEventRegistration && r.TargetScriptId == script.Id).ToArray();
                var reads = new HashSet<string>(reachable.SelectMany(s => s.Dependencies).Where(d => d.Access == "read").Select(d => d.Tag), Names);
                bool opaque = reachable.Any(s => s.Opaque || s.Calls.Any(c => c.TargetScriptId.Length == 0));
                foreach (var handler in handlers.Where(h => h.TriggerTag.Length != 0 && !reads.Contains(h.TriggerTag))) Issue("trigger-read-not-demonstrated", script.Id,
                    "Trigger tag has no demonstrated direct/transitive literal read; computed reads, aliases and control flow remain unknown.", new { handler.ResourcePath, handler.XPath, handler.TriggerTag, opaqueDependencies = opaque });
                if (handlers.Length > 1 && (reachable.Any(s => s.Loops || s.UiWrites != 0) || reachable.Sum(s => s.Dependencies.Count(d => d.Access == "write")) > 1 || reachable.Any(s => s.ClearEvidence.Count != 0)))
                    Issue("repeated-display-work-candidate", script.Id, "Multiple configured registrations reach loops/display-write evidence. Review possible queue pressure; invocation rate and overflow are unmeasured.", new { configuredHandlers = handlers.Length, continuousAcquisitionHandlers = handlers.Count(h => h.AcquisitionFields.Any(v => v.Key.EndsWith("/AcquisitionMode[1]", StringComparison.Ordinal) && new[] { "Continuous", "CyclicContinuous", "Cyclic continuous" }.Contains(v.Value, Names))) });
                bool periodic = periodicallyReachable.Contains(script.Id);
                if (handlers.Any(h => h.EventKind == "tag-change-edge") && reachable.Any(s => s.Dependencies.Any(d => d.Access == "write") || s.ClearEvidence.Count > 0 || s.UiWrites != 0) && !periodic)
                    Issue("periodic-reconciliation-not-demonstrated", script.Id, "No supplied explicit periodic registration reaches this edge-driven display candidate. Continuous acquisition plus ChangeValue is not periodic callback evidence; missing categories/scheduler/deployment remain unknown.");
                if (script.Conversions || script.Loops) Issue("conversion-loop-review", script.Id, "Conversion input validation, tag quality, loop bounds and error paths need source review; safe execution is not established.", new { script.Conversions, script.Loops, script.ResumeNext });
            }
            if (diagnostics.Count > 20000) throw new ArgumentException("Script diagnostic limit exceeded.");
            string hash = TextListDocument.Hash(ExtensionWire.Serialize(new { engine = "classic-script-trigger-v1", sources = sources.Select(s => new { s.Input.HmiId, s.Input.Path, s.Sha256 }), coverage = request.Coverage.OrderBy(c => c.HmiId, StringComparer.Ordinal).ThenBy(c => c.Category, StringComparer.Ordinal), evidence = request.SchedulerEvidence }));
            var scriptRows = scripts.Select(s => (object)new { s.Id, hmiId = s.Source.Input.HmiId, resourcePath = s.Source.Input.Path, sourceSha256 = s.Source.Sha256, s.Name, s.CodePath, codeSha256 = s.CodePath.Length == 0 ? null : TextListDocument.Hash(s.Code),
                codeAvailable = s.CodePath.Length != 0, s.Opaque, literalAccessSites = s.Dependencies.Count, uiPropertyWriteSites = s.UiWrites, callCandidates = s.Calls.Count, configuredHandlers = registrations.Count(r => r.IsEventRegistration && r.TargetScriptId == s.Id), clearWriteSites = s.ClearEvidence.Take(10).ToArray(),
                clearEvidenceTruncated = s.ClearEvidence.Count > 10, clearPathSemantics = "Write sites only: branch reachability, valid zero/replacement, quality handling, visibility relationship and callback execution are unproven." }).ToArray();
            IEnumerable<object> rows = request.View == "scripts" ? scriptRows : request.View == "registrations" ? registrations.Cast<object>() : request.View == "calls" ? scripts.SelectMany(s => s.Calls).Cast<object>() : request.View == "dependencies" ? scripts.SelectMany(s => s.Dependencies).Cast<object>() : diagnostics;
            return ExtensionWire.Json(new { summary = new { engineVersion = "classic-script-trigger-v1", analysisSha256 = hash, resources = sources.Length, scripts = scripts.Count, configuredHandlers = registrations.Count(r => r.IsEventRegistration),
                explicitScriptLinks = registrations.Count(r => r.ReferenceKind == "native-script-link"), exportedNameCandidates = registrations.Count(r => r.ReferenceKind != "native-script-link"),
                diagnostics = diagnostics.Count, schedulerStatus = "unknown", deployedRuntimeStatus = "unknown", measuredInvocationRateAvailable = false, queueOverflowEstablished = false, nativeBindingVerified = false },
                categoryCoverage = sources.Select(s => s.Input.HmiId).Distinct(StringComparer.Ordinal).SelectMany(hmi => Categories.Select(category => new { hmiId = hmi, category,
                    callerReportedStatus = request.Coverage.SingleOrDefault(c => c.HmiId == hmi && c.Category == category)?.Status ?? "unknown", observedResources = sources.Count(s => s.Input.HmiId == hmi && s.Category == category) })),
                schedulerEvidence = request.SchedulerEvidence.Select(e => new { e.Id, e.Kind, sha256 = TextListDocument.Hash(e.Content), verification = "caller-supplied, unverified; not causal/runtime acceptance" }),
                request.View, page = HmiAnalysisOperations.Page(rows, request.Offset, request.MaxItems),
                scope = "Static literal-pattern candidates from supplied classic XML only. Counts are configured handlers, not runtime rates. No execution, native import or scheduler/deployed-image validation. Preserve serial/commit-token context coherence and bounded retry when coalescing; cached-string cleanup cannot repair an unexecuted callback. GM-specific controller/banner traces are outside this generic analysis." });
        }
    }
}
