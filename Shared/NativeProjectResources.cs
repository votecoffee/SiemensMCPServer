using System;
using System.Linq;
using Siemens.Engineering;

namespace SiemensMCPServer
{
    internal static class NativeProjectResources
    {
        public static object Graphics(Project project) => ProjectGraphicsAccess.Get(project,
            name => SiemensApiTypes.Resolve(typeof(Project).Assembly, name, TiaPortalAssemblyResolver.LoadOptionalModule),
            (owner, type) => typeof(IEngineeringServiceProvider).GetMethods()
                .Single(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                .MakeGenericMethod(type).Invoke(owner, Array.Empty<object>()));
    }
}
