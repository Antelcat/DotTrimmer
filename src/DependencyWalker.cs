using Antelcat.DotTrimmer.Models;
using dnlib.DotNet;

namespace Antelcat.DotTrimmer;

public class DependencyWalker
{
    private readonly static SimpleAssemblyResolver AssemblyResolver = new();
    private readonly static ModuleContext ModuleContext = new(AssemblyResolver);

    /// <summary>
    /// 给定一个程序集列表，通过RootSettings，找出其中未被引用的程序集
    /// </summary>
    /// <param name="assemblyPaths"></param>
    /// <param name="rootSettings"></param>
    /// <returns></returns>
    public IEnumerable<string> GetUnusedAssemblies(
        IReadOnlySet<string> assemblyPaths,
        RootSettings rootSettings)
    {
        var assemblyDefs = assemblyPaths
            .Select(path => AssemblyResolver.TryLoadAssembly(path, ModuleContext))
            .OfNotNull()
            .ToHashSet();
        var assemblyGraph = new AssemblyGraph();
        foreach (var assemblyDef in assemblyDefs)
        {
            foreach (var moduleDef in assemblyDef.Modules)
            {
                assemblyGraph.TryAdd(assemblyDef, moduleDef.GetAssemblyRefs().Select(AssemblyResolver.Resolve).OfNotNull());
            }
        }
        foreach (var assemblyDef in rootSettings.Assemblies.Select(asm => new SimpleFullName(asm.Name)).Select(AssemblyResolver.Resolve).OfNotNull())
        {
            assemblyGraph.Preserve(assemblyDef);
        }
        return assemblyGraph.EnumerateNodes(true).Select(asm => AssemblyResolver.ResolveFullPath(asm)).OfNotNull();
    }

    private class SimpleFullName(UTF8String name) : IFullName
    {
        public string FullName => string.Empty;

        public UTF8String Name { get; set; } = name;
    }
}