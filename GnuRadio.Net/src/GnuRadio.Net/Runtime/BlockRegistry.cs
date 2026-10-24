using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace GnuRadio;

/// <summary>
/// Marks a block type for discovery by <see cref="BlockRegistry"/>, the .NET take on GNU Radio's out-of-tree (OOT)
/// modules: put custom blocks in any assembly, tag them, and load the assembly at runtime.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GrBlockAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public string Category { get; init; } = "Custom";
    public string Description { get; init; } = "";
}

/// <summary>A registered block type.</summary>
public sealed record BlockDescriptor(string Id, string Category, string Description, Type Type);

/// <summary>Catalogue of block types found in loaded assemblies via <see cref="GrBlockAttribute"/>.</summary>
public static class BlockRegistry
{
    private static readonly Dictionary<string, BlockDescriptor> s_blocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock s_lock = new();

    public static IReadOnlyCollection<BlockDescriptor> Blocks
    {
        get
        {
            lock (s_lock) return [.. s_blocks.Values];
        }
    }

    /// <summary>Registers every <see cref="GrBlockAttribute"/>-tagged block in <paramref name="assembly"/>.</summary>
    [RequiresUnreferencedCode("Scans assembly types with reflection.")]
    public static int Register(Assembly assembly)
    {
        var count = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (!typeof(Block).IsAssignableFrom(type) || type.IsAbstract) continue;
            var attribute = type.GetCustomAttribute<GrBlockAttribute>();
            if (attribute is null) continue;
            lock (s_lock) s_blocks[attribute.Id] = new BlockDescriptor(attribute.Id, attribute.Category, attribute.Description, type);
            count++;
        }
        return count;
    }

    /// <summary>Loads an OOT module from a path and registers its blocks.</summary>
    [RequiresUnreferencedCode("Loads and scans an assembly with reflection.")]
    public static int LoadModule(string assemblyPath) => Register(Assembly.LoadFrom(assemblyPath));

    public static bool TryGet(string id, [NotNullWhen(true)] out BlockDescriptor? descriptor)
    {
        lock (s_lock) return s_blocks.TryGetValue(id, out descriptor);
    }

    /// <summary>Creates a registered block with constructor arguments.</summary>
    [RequiresUnreferencedCode("Creates types with reflection.")]
    public static Block Create(string id, params object?[] arguments)
    {
        if (!TryGet(id, out var d)) throw new KeyNotFoundException($"No block registered as '{id}'.");
        return (Block)Activator.CreateInstance(d.Type, arguments)!;
    }
}
