using dnlib.DotNet;

namespace Antelcat.DotTrimmer;

internal class SimpleAssemblyResolver : IAssemblyResolver
{
    public IEnumerable<AssemblyDef> AssemblyDefs => assemblyCache.Values;

    private readonly Dictionary<string, AssemblyDef> assemblyCache = new();
    private readonly Dictionary<string, string> pathMap = new();

    public AssemblyDef? TryLoadAssembly(string path, ModuleContext context)
    {
        try
        {
            path = NormalizePath(path);
            var assemblyDef = AssemblyDef.Load(File.ReadAllBytes(path), context);

            foreach (var moduleDef in assemblyDef.Modules)
            {
                moduleDef.EnableTypeDefFindCache = true;
            }

            var assemblyDefName = assemblyDef.Name;
            if (assemblyCache
                    .Select(p => p.Value)
                    .FirstOrDefault(ad => ad.Name == assemblyDefName) is { } cachedAssemblyDef)
            {
                Console.WriteLine("""Assembly conflict: "{0}" and "{1}", using newer version""", cachedAssemblyDef.FullName, assemblyDef.FullName);
                if (assemblyDef.Version > cachedAssemblyDef.Version)
                {
                    assemblyCache[cachedAssemblyDef.Name] = assemblyDef;
                }
            }

            assemblyCache.Add(assemblyDef.Name, assemblyDef);
            pathMap.Add(path, assemblyDef.Name);
            return assemblyDef;
        }
        catch
        {
            return null;
        }
    }

    public void Replace(AssemblyDef assemblyDef)
    {
        assemblyCache[assemblyDef.Name] = assemblyDef;
    }

    public void Remove(AssemblyDef assemblyDef)
    {
        assemblyCache.Remove(assemblyDef.Name);
    }

    public AssemblyDef? Resolve(IAssembly assembly, ModuleDef sourceModule)
    {
        return assemblyCache.GetValueOrDefault(assembly.Name);
    }

    public AssemblyDef? Resolve(IFullName assembly)
    {
        return assemblyCache.GetValueOrDefault(assembly.Name);
    }

    public AssemblyDef? Resolve(string fullPath)
    {
        return pathMap.GetValueOrDefault(NormalizePath(fullPath)) is { } name ? 
            assemblyCache.GetValueOrDefault(name) : null;
    }

    public string? ResolveFullPath(IFullName assembly)
    {
        return pathMap.FirstOrDefault(p => p.Value == assembly.Name).Key;
    }

    private static string NormalizePath(string path)
    {
        return Environment.ExpandEnvironmentVariables(Path.GetFullPath(path)).TrimEnd('\\', '/');
    }
}