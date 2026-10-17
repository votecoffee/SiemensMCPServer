using System;
using System.Collections.Generic;

namespace SiemensMCPServer.Mcp
{
    internal sealed class ExtensionPlanBinding
    {
        public string Module = "", Generation = "", Route = "", Hash = "";
    }
    internal sealed class ExtensionPlanStore
    {
        private readonly Dictionary<string, ExtensionPlanBinding> plans = new Dictionary<string, ExtensionPlanBinding>();
        public void Add(string id, ExtensionPlanBinding plan) => plans.Add(id, plan);
        public void Clear() => plans.Clear();
        public ExtensionPlanBinding Take(string id, string hash, Func<string, string> generation)
        {
            if (!plans.TryGetValue(id, out var plan)) throw new InvalidOperationException("Unknown or invalidated extension plan; preview again.");
            if (plan.Hash != hash || generation(plan.Module) != plan.Generation) throw new InvalidOperationException("Extension generation or candidate hash changed; preview again.");
            plans.Remove(id); return plan;
        }
    }
}
