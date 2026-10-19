using System;
using System.Collections.Generic;
using System.Linq;

namespace SiemensMCPServer
{
    // Scope candidates are captured from native discovery, never supplied by a client.
    internal sealed class CompileScope
    {
        public object Target { get; }
        public string Kind { get; }
        public string Path { get; }
        public CompileScope(object target, string kind, string path) { Target = target; Kind = kind; Path = path; }
    }
    internal sealed class CompileSelection<T> where T : class
    {
        public T Service { get; }
        public CompileScope Scope { get; }
        public bool OwnerFallback => Scope.Kind != "software";
        public CompileSelection(T service, CompileScope scope) { Service = service; Scope = scope; }
    }
    internal static class CompileServiceResolver
    {
        public static CompileSelection<T> Resolve<T>(object software, string path, bool allowOwnerFallback,
            Func<object, T> service, Func<CompileScope[]> owners) where T : class
        {
            var primary = service(software);
            if (primary != null) return new CompileSelection<T>(primary, new CompileScope(software, "software", path));
            if (!allowOwnerFallback) throw new NotSupportedException("The selected engineering software does not expose an Openness compile service.");
            var scopes = owners();
            if (scopes == null || scopes.Length < 2 || scopes.Length > 9 || scopes.Any(s => s == null)
                || scopes[0].Path != path || scopes.Last().Kind != "device"
                || scopes.Take(scopes.Length - 1).Any(s => s == null || s.Kind != "deviceItem")
                || scopes.Any(s => s.Target == null || string.IsNullOrWhiteSpace(s.Path) || ReferenceEquals(s.Target, software)))
                throw new NotSupportedException("Unified compile requires a bounded native owner chain ending at its device.");
            for (int i = 0; i < scopes.Length; i++)
            {
                if (scopes.Take(i).Any(s => ReferenceEquals(s.Target, scopes[i].Target))
                    || i > 0 && !scopes[i - 1].Path.StartsWith(scopes[i].Path + "/", StringComparison.Ordinal))
                    throw new NotSupportedException("Unified compile owner identity or ancestry is inconsistent.");
            }
            foreach (var scope in scopes)
            {
                // Lookup errors propagate. A failed lookup or compile must never trigger
                // a second compile at a broader scope.
                var candidate = service(scope.Target);
                if (candidate != null) return new CompileSelection<T>(candidate, scope);
            }
            throw new NotSupportedException("Neither the selected Unified software nor its owning device exposes an Openness compile service.");
        }
    }
}
