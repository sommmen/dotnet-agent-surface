using System.Reflection;
using System.Xml.Linq;

namespace DotNetAgentSurface.Core;

public interface IOperationDocumentationSource
{
    OperationDocumentation? GetDocumentation(MethodInfo method);
}

public sealed record OperationDocumentation(
    string? Summary,
    string? Remarks,
    IReadOnlyDictionary<string, string> Parameters,
    string? Returns);

public sealed class OperationDocumentationOptions
{
    public bool IncludeRemarks { get; set; }

    public bool RequireDescription { get; set; }
}

public sealed record OperationDocumentationDiagnostic(string OperationName, string Message);

public sealed class NullOperationDocumentationSource : IOperationDocumentationSource
{
    public static NullOperationDocumentationSource Instance { get; } = new();

    private NullOperationDocumentationSource()
    {
    }

    public OperationDocumentation? GetDocumentation(MethodInfo method) => null;
}

public sealed class CompositeOperationDocumentationSource : IOperationDocumentationSource
{
    private readonly IReadOnlyList<IOperationDocumentationSource> _sources;

    public CompositeOperationDocumentationSource(params IOperationDocumentationSource[] sources)
    {
        Guard.ThrowIfNull(sources);
        _sources = Array.AsReadOnly(sources.Where(static source => source is not null).ToArray());
    }

    public OperationDocumentation? GetDocumentation(MethodInfo method)
    {
        Guard.ThrowIfNull(method);
        return _sources.Select(source => source.GetDocumentation(method)).FirstOrDefault(static documentation => documentation is not null);
    }
}

public sealed class XmlOperationDocumentationSource : IOperationDocumentationSource
{
    private readonly IReadOnlyList<string> _paths;
    private readonly Dictionary<Assembly, IReadOnlyDictionary<string, XElement>> _members = new();

    public XmlOperationDocumentationSource(params string[] paths)
    {
        Guard.ThrowIfNull(paths);
        _paths = Array.AsReadOnly(paths.Where(static path => !string.IsNullOrWhiteSpace(path)).ToArray());
    }

    public static XmlOperationDocumentationSource ProbeAppBaseDirectory() => new();

    public OperationDocumentation? GetDocumentation(MethodInfo method)
    {
        Guard.ThrowIfNull(method);
        return GetDocumentation(method, []);
    }

    private OperationDocumentation? GetDocumentation(MethodInfo method, HashSet<MethodInfo> visited)
    {
        if (!visited.Add(method))
        {
            // Defensive cycle guard; reflection cannot actually produce a base-definition cycle.
            return null;
        }

        var id = DocumentationCommentId.Get(method);
        if (id is null || !GetMembers(method.DeclaringType!.Assembly).TryGetValue(id, out var member))
        {
            return null;
        }

        if (member.Element("inheritdoc") is not null)
        {
            var inheritedFrom = FindSingleInheritedMethod(method);
            return inheritedFrom is null ? null : GetDocumentation(inheritedFrom, visited);
        }

        var parameters = member.Elements("param")
            .Where(element => !string.IsNullOrWhiteSpace((string?)element.Attribute("name")))
            .Select(element => (Name: (string)element.Attribute("name")!, Text: DocumentationText.Normalize(element)))
            .Where(entry => entry.Text is not null)
            .ToDictionary(
                entry => entry.Name,
                entry => entry.Text!,
                StringComparer.Ordinal);

        return new OperationDocumentation(
            DocumentationText.Normalize(member.Element("summary")),
            DocumentationText.Normalize(member.Element("remarks")),
            parameters,
            DocumentationText.Normalize(member.Element("returns")));
    }

    /// <summary>
    /// Resolves the trivial, unambiguous <c>&lt;inheritdoc/&gt;</c> target for <paramref name="method"/>:
    /// the single base class member it overrides, or the single interface member it implements. Any other
    /// shape (no base/interface member, or more than one implemented interface member) is treated as "no
    /// documentation" rather than guessed at; full <c>cref</c>/<c>path</c> resolution is out of scope.
    /// </summary>
    private static MethodInfo? FindSingleInheritedMethod(MethodInfo method)
    {
        var baseDefinition = method.GetBaseDefinition();
        if (!Equals(baseDefinition, method))
        {
            return baseDefinition;
        }

        var declaringType = method.DeclaringType;
        if (declaringType is null)
        {
            return null;
        }

        MethodInfo? found = null;
        foreach (var interfaceType in declaringType.GetInterfaces())
        {
            var map = declaringType.GetInterfaceMap(interfaceType);
            for (var i = 0; i < map.TargetMethods.Length; i++)
            {
                if (!Equals(map.TargetMethods[i], method))
                {
                    continue;
                }

                if (found is not null)
                {
                    // Ambiguous: the method implements more than one interface member.
                    return null;
                }

                found = map.InterfaceMethods[i];
            }
        }

        return found;
    }

    private IReadOnlyDictionary<string, XElement> GetMembers(Assembly assembly)
    {
        if (_members.TryGetValue(assembly, out var members))
        {
            return members;
        }

        var paths = _paths.Count > 0
            ? _paths
            : [Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".xml")];
        var loaded = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var document = XDocument.Load(path, LoadOptions.None);
                foreach (var member in document.Descendants("member"))
                {
                    var name = (string?)member.Attribute("name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        loaded[name!] = member;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Xml.XmlException)
            {
                // XML documentation is optional; unreadable sources are a cacheable miss.
            }
        }

        members = loaded;
        _members[assembly] = members;
        return members;
    }
}

internal static class DocumentationText
{
    public static string? Normalize(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var text = string.Concat(element.Nodes().Select(RenderNode));
        text = System.Text.RegularExpressions.Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string RenderNode(XNode node) => node switch
    {
        XText text => text.Value,
        XElement element when element.Name.LocalName is "see" or "seealso" => ReadableReference((string?)element.Attribute("cref")) ?? element.Value,
        XElement element when element.Name.LocalName is "paramref" or "typeparamref" => (string?)element.Attribute("name") ?? string.Empty,
        XElement element => string.Concat(element.Nodes().Select(RenderNode)),
        _ => string.Empty
    };

    private static string? ReadableReference(string? reference)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var value = reference.Length > 2 && reference[1] == ':' ? reference[2..] : reference;
        var lastDot = value.LastIndexOf('.');
        return (lastDot >= 0 ? value[(lastDot + 1)..] : value).Replace('#', '.');
    }
}

internal static class DocumentationCommentId
{
    public static string? Get(MethodInfo method)
    {
        try
        {
            var declaringType = method.DeclaringType;
            if (declaringType is null) return null;
            var name = "M:" + TypeName(declaringType) + "." + method.Name;
            if (method.IsGenericMethodDefinition) name += "``" + method.GetGenericArguments().Length;
            var parameters = method.GetParameters();
            if (parameters.Length > 0) name += "(" + string.Join(",", parameters.Select(ParameterTypeName)) + ")";
            if (method.Name is "op_Implicit" or "op_Explicit") name += "~" + TypeName(method.ReturnType);
            return name;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string ParameterTypeName(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var suffix = type.IsByRef ? "@" : string.Empty;
        return TypeName(type.IsByRef ? type.GetElementType()! : type) + suffix;
    }

    private static string TypeName(Type type)
    {
        if (type.IsGenericParameter) return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        if (type.IsArray) return TypeName(type.GetElementType()!) + (type.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]");
        if (type.IsPointer) return TypeName(type.GetElementType()!) + "*";
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = (definition.FullName ?? definition.Name).Replace('+', '.');
            return name + "{" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + "}";
        }

        return (type.FullName ?? type.Name).Replace('+', '.');
    }
}
