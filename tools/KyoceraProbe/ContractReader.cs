using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace ScanRelay.KyoceraProbe;

/// <summary>
/// Reads the shape of the address book messages out of KYOCERA Net Viewer's program files:
/// which fields each request and response has, in what order, and what values are allowed.
/// It reads the files as data. Nothing from Net Viewer is run, and no code is copied.
/// </summary>
public static partial class ContractReader
{
    [GeneratedRegex("personal_address|one_touch_key|_information$|login_system|logout_system|authentication_status|service_information|^(name|email|smb|ftp|fax|ifax)_|RESULT|result", RegexOptions.IgnoreCase)]
    private static partial Regex Wanted();

    [GeneratedRegex("address_book|authentication_authorization|device_information", RegexOptions.IgnoreCase)]
    private static partial Regex WantedNamespace();

    public static void Write(string folder, TextWriter report)
    {
        if (!Directory.Exists(folder))
        {
            report.WriteLine("  That folder does not exist.");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".exe").Order())
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var lines = Describe(pe.GetMetadataReader());
                if (lines.Count == 0) continue;
                report.WriteLine();
                report.WriteLine($"--- {Path.GetFileName(file)} ---");
                foreach (var line in lines) report.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
            {
            }
        }
    }

    public static List<string> Describe(MetadataReader md)
    {
        var provider = new Names(md);
        var all = md.TypeDefinitions.Select(h => (Handle: h, Type: md.GetTypeDefinition(h))).ToList();
        string NameOf(TypeDefinition t) => md.GetString(t.Name);
        string SpaceOf(TypeDefinition t) => md.GetString(t.Namespace);

        // Start from the message types, then follow their fields to everything they are made of.
        var chosen = new Dictionary<string, TypeDefinition>();
        var queue = new Queue<TypeDefinition>(all
            .Where(x => Wanted().IsMatch(NameOf(x.Type)) && (WantedNamespace().IsMatch(SpaceOf(x.Type)) || NameOf(x.Type).Contains("personal_address") || NameOf(x.Type).Contains("one_touch_key")))
            .Select(x => x.Type));
        var byName = all.GroupBy(x => SpaceOf(x.Type) + "." + NameOf(x.Type)).ToDictionary(g => g.Key, g => g.First().Type);
        while (queue.Count > 0 && chosen.Count < 400)
        {
            var type = queue.Dequeue();
            var key = SpaceOf(type) + "." + NameOf(type);
            if (!chosen.TryAdd(key, type)) continue;
            foreach (var fieldHandle in type.GetFields())
            {
                var fieldType = md.GetFieldDefinition(fieldHandle).DecodeSignature(provider, null).TrimEnd('[', ']');
                if (byName.TryGetValue(fieldType, out var next) && !chosen.ContainsKey(fieldType)) queue.Enqueue(next);
            }
        }

        var lines = new List<string>();
        foreach (var (key, type) in chosen.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var isEnum = !type.BaseType.IsNil && provider.NameOf(type.BaseType) == "System.Enum";
            lines.Add($"{(isEnum ? "enum" : "type")} {key}{Attributes(md, type.GetCustomAttributes(), provider)}");
            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                var name = md.GetString(field.Name);
                if (isEnum)
                {
                    if ((field.Attributes & FieldAttributes.Literal) != 0) lines.Add($"    {name}{Attributes(md, field.GetCustomAttributes(), provider)}");
                    continue;
                }

                if ((field.Attributes & FieldAttributes.Static) != 0) continue;
                lines.Add($"    {field.DecodeSignature(provider, null)} {name}{Attributes(md, field.GetCustomAttributes(), provider)}");
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = md.GetPropertyDefinition(propertyHandle);
                var attributes = Attributes(md, property.GetCustomAttributes(), provider);
                if (attributes.Length > 0) lines.Add($"    property {md.GetString(property.Name)}{attributes}");
            }
        }

        return lines;
    }

    /// <summary>The XML and message attributes on a member, which say what it is called on the wire and where it goes.</summary>
    private static string Attributes(MetadataReader md, CustomAttributeHandleCollection handles, Names provider)
    {
        var parts = new List<string>();
        foreach (var handle in handles)
        {
            var attribute = md.GetCustomAttribute(handle);
            var name = attribute.Constructor.Kind == HandleKind.MemberReference
                ? provider.NameOf(md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent)
                : provider.NameOf(md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType());
            var shortName = name[(name.LastIndexOf('.') + 1)..].Replace("Attribute", "");
            if (!shortName.StartsWith("Xml", StringComparison.Ordinal) && !shortName.StartsWith("Message", StringComparison.Ordinal)
                && !shortName.StartsWith("Soap", StringComparison.Ordinal) && !shortName.StartsWith("OperationContract", StringComparison.Ordinal)
                && !shortName.StartsWith("ServiceContract", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var value = attribute.DecodeValue(provider);
                var arguments = value.FixedArguments.Select(a => Show(a.Value))
                    .Concat(value.NamedArguments.Select(a => $"{a.Name}={Show(a.Value)}"));
                parts.Add($"[{shortName}({string.Join(", ", arguments)})]");
            }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                parts.Add($"[{shortName}]");
            }
        }

        return parts.Count == 0 ? "" : "  " + string.Join(" ", parts);
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        ImmutableArray<CustomAttributeTypedArgument<string>> array => "[" + string.Join(", ", array.Select(a => Show(a.Value))) + "]",
        _ => value.ToString() ?? "",
    };

    /// <summary>Turns type references in the files into readable names.</summary>
    private sealed class Names(MetadataReader md) : ISignatureTypeProvider<string, object?>, ICustomAttributeTypeProvider<string>
    {
        public string NameOf(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
            _ => "?",
        };

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString().ToLowerInvariant();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var space = reader.GetString(type.Namespace);
            return (space.Length > 0 ? space + "." : "") + reader.GetString(type.Name);
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            var space = reader.GetString(type.Namespace);
            return (space.Length > 0 ? space + "." : "") + reader.GetString(type.Name);
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "function";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => "T" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "T" + index;

        public string GetSystemType() => "System.Type";
        public bool IsSystemType(string type) => type == "System.Type";
        public string GetTypeFromSerializedName(string name) => name;

        // Attribute arguments that are enums are stored as numbers. Four bytes is right for every attribute read here.
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    }
}
