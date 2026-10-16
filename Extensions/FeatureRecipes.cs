using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using SiemensTiaOpenness.Mcp;

namespace SiemensTiaOpenness.Extensions
{
    internal sealed class FeatureRecipe
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string XPath { get; set; } = "";
        public string NewValue { get; set; } = "";
    }
    internal sealed class FeatureRecipes
    {
        private readonly FeatureRecipe[] recipes;
        public FeatureRecipes(string module, string? json)
        {
            recipes = json == null ? Array.Empty<FeatureRecipe>() : JsonSerializer.Deserialize<FeatureRecipe[]>(json, ExtensionWire.Options) ?? throw new ArgumentException("Invalid recipes.");
            if (recipes.Length > 32 || recipes.Length != 0 && module != "comfort" || recipes.Any(r => r == null || !Regex.IsMatch(r.Name ?? "", "^[a-z][a-z0-9_]{0,63}$")
                || r.Description == null || r.Description.Length > 1024 || string.IsNullOrEmpty(r.XPath) || r.XPath.Length > 2048 || r.NewValue == null || r.NewValue.Length > 65535)
                || recipes.GroupBy(r => r.Name, StringComparer.Ordinal).Any(g => g.Count() > 1) || recipes.Any(r => FeatureOperations.Describe(module).Any(o => o.Name == r.Name)))
                throw new ArgumentException("Invalid or conflicting classic XML recipe.");
        }
        public ExtensionOperation[] Operations => recipes.Select(r => new ExtensionOperation { Name = r.Name, Description = r.Description, Route = "classicXml", Preview = true,
            InputSchema = ExtensionWire.Json(new { type = "object", required = new[] { "expectedValue" }, properties = new { expectedValue = new { type = "string" } },
                preset = new { xPath = r.XPath, newValue = r.NewValue } }) }).ToArray();
        public bool TryExecute(string name, JsonElement input, out JsonElement result)
        {
            var recipe = recipes.SingleOrDefault(r => r.Name == name); result = default;
            if (recipe == null) return false;
            var edit = new ClassicXmlEdit { XPath = recipe.XPath, ExpectedValue = input.GetProperty("arguments").GetProperty("expectedValue").GetString()!, NewValue = recipe.NewValue };
            string xml = new ClassicXmlDocument(input.GetProperty("snapshot").GetString()!).Edit(new[] { edit });
            result = ExtensionWire.Json(new { candidateXml = xml, changes = new[] { edit } }); return true;
        }
    }
}
