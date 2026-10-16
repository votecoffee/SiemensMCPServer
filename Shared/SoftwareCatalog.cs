using System;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace SiemensTiaOpenness
{
    internal sealed class SoftwareReference
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public Software Software { get; set; }
        public string Family { get; set; }
        public DeviceItem Owner { get; set; }
    }
    // Includes PLCs without changing the desktop's classic-only HmiCatalog contract or IDs.
    internal static class SoftwareCatalog
    {
        public static IList<SoftwareReference> Discover(Project project)
        {
            var result = new List<SoftwareReference>(); var seen = new HashSet<Software>();
            foreach (Device device in project.Devices) VisitDevice(device, "devices", result, seen);
            foreach (DeviceUserGroup group in project.DeviceGroups) VisitGroup(group, "groups", result, seen);
            if (project.UngroupedDevicesGroup != null)
                foreach (Device device in project.UngroupedDevicesGroup.Devices) VisitDevice(device, "ungrouped", result, seen);
            return result;
        }
        private static void VisitGroup(DeviceUserGroup group, string path, List<SoftwareReference> result, HashSet<Software> seen)
        {
            path += "/" + Uri.EscapeDataString(group.Name);
            foreach (Device device in group.Devices) VisitDevice(device, path, result, seen);
            foreach (DeviceUserGroup child in group.Groups) VisitGroup(child, path, result, seen);
        }
        private static void VisitDevice(Device device, string path, List<SoftwareReference> result, HashSet<Software> seen)
        {
            path += "/" + Uri.EscapeDataString(device.Name);
            foreach (DeviceItem item in device.DeviceItems) VisitItem(item, path, result, seen);
        }
        private static void VisitItem(DeviceItem item, string path, List<SoftwareReference> result, HashSet<Software> seen)
        {
            path += "/" + Uri.EscapeDataString(item.Name);
            var container = item.GetService<SoftwareContainer>(); var software = container == null ? null : container.Software;
            string family = software is HmiTarget ? "classic" : software is PlcSoftware ? "plc"
                : software != null && HmiCatalog.IsUnifiedType(software.GetType()) ? "unified" : null;
            if (family != null && seen.Add(software)) result.Add(new SoftwareReference { Id = path, Label = Uri.UnescapeDataString(path), Software = software, Family = family, Owner = item });
            foreach (DeviceItem child in item.DeviceItems) VisitItem(child, path, result, seen);
        }
    }
}
