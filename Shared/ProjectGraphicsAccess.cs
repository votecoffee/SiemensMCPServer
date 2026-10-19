using System;
using System.Reflection;

namespace SiemensMCPServer
{
    internal static class ProjectGraphicsAccess
    {
        public const string ProviderType = "Siemens.Engineering.Hmi.Globalization.GraphicsProvider";

        public static object Get(object project, Func<string, Type?> resolveType, Func<object, Type, object?> getService)
        {
            PropertyInfo? legacy = project.GetType().GetProperty("Graphics", BindingFlags.Public | BindingFlags.Instance);
            if (legacy != null) return ReadGraphics(project, legacy);

            Type type = resolveType(ProviderType)
                ?? throw new NotSupportedException("The selected Openness API does not expose project graphics or GraphicsProvider.");
            object service = getService(project, type)
                ?? throw new NotSupportedException("The selected project does not provide the Openness GraphicsProvider service.");
            if (!type.IsInstanceOfType(service)) throw new InvalidOperationException("The project graphics service has an unexpected type.");
            PropertyInfo graphics = type.GetProperty("Graphics", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new NotSupportedException("The installed GraphicsProvider does not expose Graphics.");
            return ReadGraphics(service, graphics);
        }

        private static object ReadGraphics(object owner, PropertyInfo property)
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
                throw new NotSupportedException("The installed project graphics property is not readable.");
            return property.GetValue(owner, null)
                ?? throw new NotSupportedException("The selected project does not expose a graphics collection.");
        }
    }
}
