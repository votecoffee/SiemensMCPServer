using System;
using System.Collections.Generic;
using System.Linq;

namespace SiemensMCPServer.Analysis
{
    internal static class HmiBindingValidator
    {
        private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
        internal static HmiConnectionMapping? Mapping(HmiConnectionMapping[] mappings, string hmi, string connection)
            => mappings.SingleOrDefault(m => Names.Equals(m.HmiId, hmi) && Names.Equals(m.Connection, connection))
                ?? mappings.SingleOrDefault(m => m.HmiId.Length == 0 && Names.Equals(m.Connection, connection));
        public static HmiBindingValidation[] Validate(HmiTagDeclaration[] tags, ControllerDeclaration[] controllers, HmiConnectionMapping[] mappings)
        {
            var declarations = controllers.ToDictionary(c => c.Id, Names);
            var resolvers = controllers.ToDictionary(c => c.Id, c => new ControllerSymbolResolver(c), Names);
            var duplicates = new HashSet<string>(tags.GroupBy(t => t.HmiId + "\n" + t.Name, Names).Where(g => g.Count() > 1).Select(g => g.Key), Names);
            return tags.Select(tag => {
                var result = new HmiBindingValidation { TagId = tag.Id, HmiId = tag.HmiId, Tag = tag.Name, Connection = tag.Connection,
                    ControllerAddress = tag.ControllerSymbol.Length == 0 ? tag.LogicalAddress : tag.ControllerSymbol };
                HmiBindingValidation Fail(string status, params string[] reasons) { result.Status = status; result.Issues = reasons; return result; }
                if (duplicates.Contains(tag.HmiId + "\n" + tag.Name)) return Fail("ambiguousTag", "HMI tag qualified name is duplicated.");
                if (tag.Issues.Length != 0) return Fail("unsupportedTag", tag.Issues);
                if (tag.Multiplexed) return Fail("multiplexed", "Multiplexed HMI addressing requires runtime selection; only static possible dependencies can be reported.");
                if (tag.AliasTag.Length != 0) return Fail("hmiAliasUnsupported", "A direct HMI tag-to-tag binding requires its own native alias adapter.");
                if (tag.Connection.Length == 0 && result.ControllerAddress.Length == 0) { result.Status = "internal"; return result; }
                if (tag.Connection.Length == 0 || result.ControllerAddress.Length == 0) return Fail("incompleteBinding", "External HMI bindings require a connection and controller address.");
                if (tag.ControllerSymbol.Length != 0 && tag.LogicalAddress.Length != 0) return Fail("conflictingAddress", "Both ControllerTag and LogicalAddress are populated; select the native authoritative address explicitly.");
                var mapping = Mapping(mappings, tag.HmiId, tag.Connection);
                if (mapping == null) return Fail("unmappedConnection", "No explicit HMI connection-to-controller metadata mapping was supplied.");
                result.ControllerId = mapping.ControllerId;
                if (!declarations.TryGetValue(mapping.ControllerId, out var declaration)) return Fail("missingController", "The mapped controller declaration is absent.");
                if (mapping.ExpectedControllerName.Length != 0 && !Names.Equals(mapping.ExpectedControllerName, declaration.Name))
                    return Fail("controllerIdentityMismatch", "The imported controller name differs from the explicit connection mapping expectation.");
                var resolved = resolvers[mapping.ControllerId].Resolve(result.ControllerAddress, mapping.DefaultScope);
                result.Resolution = resolved;
                if (resolved.Status != "resolved") return Fail(resolved.Status, resolved.Reason);
                if (resolved.ExternalAccess.Equals("None", StringComparison.OrdinalIgnoreCase)) return Fail("externalAccessNone", "The controller declaration prohibits external access.");
                if (tag.DataType.Length == 0 || !ControllerSymbolResolver.IsAtomic(tag.DataType)) return Fail("unsupportedHmiType", "The HMI base type is missing or requires another adapter.");
                if (ControllerSymbolResolver.Atomic(tag.DataType) != ControllerSymbolResolver.Atomic(resolved.DataType))
                    return Fail("typeMismatch", "HMI controller-transfer type " + tag.DataType + " differs from declared controller type " + resolved.DataType + ".");
                if (tag.Dimensions.Length != resolved.Dimensions.Length) return Fail("arrayRankMismatch", "HMI and controller array ranks differ.");
                for (int axis = 0; axis < tag.Dimensions.Length; axis++)
                    if (tag.Dimensions[axis].Lower != resolved.Dimensions[axis].Lower || tag.Dimensions[axis].Upper != resolved.Dimensions[axis].Upper)
                        return Fail("arrayBoundsMismatch", "HMI and controller array bounds differ; offset conversion is not inferred.");
                var warnings = new List<string>();
                if (resolved.ExternalAccess.Equals("Read Only", StringComparison.OrdinalIgnoreCase)) warnings.Add("Controller declaration permits read-only access; write consumers must be reviewed.");
                if (tag.HmiDataType.Length != 0 && HmiReferenceAnalyzer.TypeShape(tag.HmiDataType).type != tag.DataType) warnings.Add("Native HMI storage/transfer types differ; runtime conversion is not validated.");
                if (tag.DataType == "STRING" || tag.DataType == "WSTRING") warnings.Add("String encoding/capacity and driver conversion require separate native validation.");
                if (declaration.PartialExport) warnings.Add("Controller metadata is a partial/context or PLCopen declaration export; absent declarations do not prove absence in the controller.");
                result.Status = "valid"; result.Warnings = warnings.ToArray(); return result;
            }).ToArray();
        }
    }
}
