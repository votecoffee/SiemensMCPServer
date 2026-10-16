using System;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace SiemensTiaOpenness
{
    internal sealed class HmiReference
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public Software Software { get; set; }
        public HmiTarget Target { get { return Software as HmiTarget; } }
        public string Family { get; set; }
    }

    // Shared by the desktop interface and the headless service.
    internal static class HmiCatalog
    {
        public static IList<HmiReference> Discover(Project project, bool includeUnified = false)
        {
            var result = new List<HmiReference>();
            var seen = new HashSet<Software>();
            foreach (Device device in project.Devices)
                VisitDevice(device, "devices", result, seen, includeUnified);
            foreach (DeviceUserGroup group in project.DeviceGroups)
                VisitGroup(group, "groups", result, seen, includeUnified);
            if (project.UngroupedDevicesGroup != null)
                foreach (Device device in project.UngroupedDevicesGroup.Devices)
                    VisitDevice(device, "ungrouped", result, seen, includeUnified);
            return result;
        }

        private static void VisitGroup(DeviceUserGroup group, string path, List<HmiReference> result, HashSet<Software> seen, bool includeUnified)
        {
            path += "/" + Uri.EscapeDataString(group.Name);
            foreach (Device device in group.Devices) VisitDevice(device, path, result, seen, includeUnified);
            foreach (DeviceUserGroup child in group.Groups) VisitGroup(child, path, result, seen, includeUnified);
        }

        private static void VisitDevice(Device device, string path, List<HmiReference> result, HashSet<Software> seen, bool includeUnified)
        {
            path += "/" + Uri.EscapeDataString(device.Name);
            foreach (DeviceItem item in device.DeviceItems) VisitItem(item, path, result, seen, includeUnified);
        }

        private static void VisitItem(DeviceItem item, string path, List<HmiReference> result, HashSet<Software> seen, bool includeUnified)
        {
            path += "/" + Uri.EscapeDataString(item.Name);
            var container = item.GetService<SoftwareContainer>();
            var software = container == null ? null : container.Software;
            string family = software is HmiTarget ? "classic" : software != null && IsUnifiedType(software.GetType()) ? "unified" : null;
            if (family != null && (family == "classic" || includeUnified) && seen.Add(software))
                result.Add(new HmiReference { Id = path, Label = Uri.UnescapeDataString(path), Software = software, Family = family });
            foreach (DeviceItem child in item.DeviceItems) VisitItem(child, path, result, seen, includeUnified);
        }

        internal static bool IsUnifiedType(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (current.FullName == "Siemens.Engineering.HmiUnified.HmiSoftware") return true;
            return false;
        }
    }
}
