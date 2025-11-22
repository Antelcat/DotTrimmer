using System.Diagnostics;
using System.Xml.Serialization;
using Antelcat.DotTrimmer.Models;
using dnlib.DotNet;

namespace Antelcat.DotTrimmer;

public class Trimmer
{
    private readonly SimpleAssemblyResolver assemblyResolver = new();
    private readonly ModuleContext moduleContext;

    public Trimmer()
    {
        moduleContext = new ModuleContext(assemblyResolver);
    }

    public interface IProgressTask : IDisposable
    {
        int Current { get; set; }
    }

    public delegate IProgressTask ProgressFactory(string message, int total);

    /// <summary>
    /// Trims the assemblies by removing unused types.
    /// </summary>
    /// <param name="configuration">Configuration for the trimmer.</param>
    /// <param name="progressFactory">Factory to create progress reporter.</param>
    /// <param name="logger">Logger action.</param>
    public async Task TrimAsync(
        TrimmerConfiguration configuration,
        ProgressFactory? progressFactory = null,
        Action<string>? logger = null)
    {
        var rootSettings = configuration.RootSettings ?? throw new ArgumentNullException(nameof(configuration.RootSettings));
        var typeDefsSet = LoadTypeDefs(configuration);
        var allowedAssembliesSet = LoadAllowedAssemblies(configuration);
        var outputPath = configuration.OutputDirectory ?? configuration.InputDirectory;

        // For each type in assemblyDef, if it is not referenced by any other type, remove it
        // The algorithm needs to build multiple directed graphs, considering generics, etc.
        // Traverse each directed graph, if any node in it is in rootTypes, then all nodes in this graph will be marked as preserved
        // Afterwards, re-traverse each directed graph, if any node in it is marked as preserved, then all nodes in this graph will be marked as preserved
        // Repeat this process until no new nodes are marked as preserved
        // Afterwards, remove all nodes that are not marked as preserved and are in allowedTypeDefs
        var typeGraph = new TypeGraph();
        
        using (var progress = progressFactory?.Invoke("Building type graph...", typeDefsSet.Count))
        {
            await Parallel.ForEachAsync(typeDefsSet,
                (typeDef, _) =>
                {
                    typeGraph.TryAdd(typeDef, typeDef.ResolveDependencies());
                    if (progress != null) progress.Current++;
                    return ValueTask.CompletedTask;
                });
        }

        IEnumerable<TypeDef> ResolveRootTypeDefs()
        {
            foreach (var typeDef in typeDefsSet)
            {
                var assembly = rootSettings.Assemblies.FirstOrDefault(a => a.Name == typeDef.Module.Assembly.FullName);
                if (assembly == null) continue;
                var type = assembly.Types.FirstOrDefault(t => t.Name == typeDef.ReflectionFullName);
                if (assembly.PreserveMode == RootSettings.PreserveMode.All && type is not { PreserveMode: RootSettings.PreserveMode.None })
                {
                    yield return typeDef;
                }
            }
        }

        var rootTypeDefs = ResolveRootTypeDefs().ToHashSet();
        logger?.Invoke("Preserving types...");
        // Preserve CLR core types
        foreach (var typeDef in typeDefsSet.Where(t => t.IsRuntimeSpecialName))
        {
            typeGraph.Preserve(typeDef);
        }

        // Preserve types referenced by RootTypes
        foreach (var rootTypeDef in rootTypeDefs)
        {
            typeGraph.Preserve(rootTypeDef);
        }

        // Preserve types referenced by custom attributes in assemblies and modules
        foreach (var customAttributeTypeDef in assemblyResolver.AssemblyDefs
                     .SelectMany(ad => ad.CustomAttributes)
                     .Concat(assemblyResolver.AssemblyDefs
                         .SelectMany(ad => ad.Modules)
                         .SelectMany(moduleDef => moduleDef.CustomAttributes))
                     .SelectMany(ca => ca.ResolveCustomAttribute()))
        {
            typeGraph.Preserve(customAttributeTypeDef);
        }

        logger?.Invoke("Removing unused types...");
        foreach (var typeDef in allowedAssembliesSet
                     .Select(assemblyDef => typeDefsSet.Where(typeDef => typeDef.DefinitionAssembly == assemblyDef).ToHashSet())
                     .SelectMany(assemblyTypeDefs =>
                         typeGraph.EnumerateNodes(false).Where(assemblyTypeDefs.Contains))
                     .Where(t => t.Name != "<Module>"))
        {
            if (typeDef.DeclaringType is { } parentTypeDef)
            {
                Debug.Assert(parentTypeDef.NestedTypes.Remove(typeDef));
            }
            else
            {
                Debug.Assert(typeDef.Module.Types.Remove(typeDef));
            }
        }

        var typeRefs = allowedAssembliesSet.SelectMany(assemblyDef => assemblyDef.Modules.SelectMany(m => m.GetTypeRefs())).ToList();
        using (var progress = progressFactory?.Invoke("Rebuilding forwarded TypeRefs...", typeRefs.Count))
        {
            foreach (var moduleDef in allowedAssembliesSet.SelectMany(assemblyDef => assemblyDef.Modules))
            {
                moduleDef.EnableTypeDefFindCache = false; // Disable cache to allow modification
            }

            var typeForwardCache = new Dictionary<AssemblyDef, Dictionary<string, IAssembly>>();
            foreach (var typeRef in typeRefs)
            {
                var assemblyRef = typeRef.Scope as IAssembly;
                while (assemblyRef != null) // Handle multiple forwarding
                {
                    if (assemblyRef.IsCorLib() || // e.g. netstandard forwards many types, no need to handle
                        assemblyResolver.Resolve(assemblyRef) is not { } assemblyDef)
                    {
                        assemblyRef = null;
                        break;
                    }

                    if (assemblyDef.Find(typeRef) != null) break;

                    if (!typeForwardCache.TryGetValue(assemblyDef, out var cache))
                    {
                        cache = new Dictionary<string, IAssembly>();
                        foreach (var moduleDef in assemblyDef.Modules)
                        {
                            foreach (var exportedType in moduleDef.ExportedTypes)
                            {
                                if (assemblyResolver.Resolve(exportedType.DefinitionAssembly) == null) continue;
                                cache.Add(exportedType.FullName, exportedType.DefinitionAssembly);
                            }
                        }
                        typeForwardCache.Add(assemblyDef, cache);
                    }

                    cache.TryGetValue(typeRef.FullName, out assemblyRef);
                }

                if (progress != null) progress.Current++;

                if (assemblyRef == null) continue;
                if (assemblyRef == typeRef.DefinitionAssembly) continue;
                Debug.Assert(assemblyRef.FullName != typeRef.DefinitionAssembly.FullName);
                // logger?.Invoke($"Redirecting \"{typeRef.FullName}\" from \"{typeRef.DefinitionAssembly.Name}\" to \"{assemblyRef.Name}\"...");
                typeRef.ResolutionScope = new AssemblyRefUser(assemblyRef);
            }
        }

        using (var progress = progressFactory?.Invoke("Rebuilding metadata...", allowedAssembliesSet.Count))
        {
            foreach (var assemblyDef in allowedAssembliesSet.ToList())
            {
                // Write out and re-read to rebuild metadata
                assemblyResolver.Remove(assemblyDef);
                allowedAssembliesSet.Remove(assemblyDef);
                typeDefsSet.RemoveWhere(typeDef => assemblyDef.Modules.Contains(typeDef.Module));
                
                using var memoryStream = new MemoryStream();
                assemblyDef.Write(memoryStream);
                memoryStream.Position = 0;
                var newAssemblyDef = AssemblyDef.Load(memoryStream, moduleContext);
                foreach (var moduleDef in newAssemblyDef.Modules)
                {
                    moduleDef.EnableTypeDefFindCache = true;
                }
                assemblyResolver.Replace(newAssemblyDef);
                allowedAssembliesSet.Add(newAssemblyDef);
                foreach (var typeDef in newAssemblyDef.Modules.SelectMany(m => m.GetTypes()))
                {
                    typeDefsSet.Add(typeDef);
                }
                
                if (progress != null) progress.Current++;
            }
            
            rootTypeDefs = ResolveRootTypeDefs().ToHashSet();
        }
        
        logger?.Invoke("Removing unused assemblies...");
        var assemblyGraph = new AssemblyGraph();
        foreach (var moduleDef in allowedAssembliesSet.SelectMany(assemblyDef => assemblyDef.Modules))
        {
            assemblyGraph.TryAdd(
                moduleDef.Assembly,
                moduleDef.GetAssemblyRefs()
                    .Select(assemblyRef => assemblyResolver.Resolve(assemblyRef))
                    .OfNotNull());
        }
        foreach (var rootTypeDef in rootTypeDefs)
        {
            assemblyGraph.Preserve(rootTypeDef.Module.Assembly);
        }
        foreach (var rootTypeRef in rootTypeDefs.SelectMany(typeDef => typeDef.Module.GetTypeRefs()))
        {
            if (assemblyResolver.Resolve(rootTypeRef.DefinitionAssembly) is { } assemblyDef)
            {
                assemblyGraph.Preserve(assemblyDef);
            }
        }
        foreach (var assemblyDef in assemblyGraph.EnumerateNodes(false))
        {
            logger?.Invoke($"Assembly \"{assemblyDef.FullName}\" is no longer used");
        }

        var trimmedAssemblies = assemblyGraph.EnumerateNodes(true).ToList();
        using (var progress = progressFactory?.Invoke("Writing trimmed assemblies...", trimmedAssemblies.Count))
        {
            outputPath = SimpleAssemblyResolver.NormalizePath(outputPath);
            Directory.CreateDirectory(outputPath);
            foreach (var assemblyDef in trimmedAssemblies)
            {
                var assemblyInputPath = assemblyResolver.ResolveFullPath(assemblyDef) ??
                                        throw new Exception($"Cannot resolve full path of assembly: {assemblyDef.FullName}");
                var assemblyOutputPath = Path.Combine(outputPath, Path.GetFileName(assemblyInputPath));
                try
                {
                    assemblyDef.Write(assemblyOutputPath);
                }
                catch (Exception e)
                {
                    logger?.Invoke($"Error when writing trimmed assembly: {assemblyDef.FullName}. {e.Message}");
                }

                if (progress != null) progress.Current++;
            }
        }
        
        logger?.Invoke("Done.");
    }

    private HashSet<TypeDef> LoadTypeDefs(TrimmerConfiguration configuration)
    {
        var typeDefs = new HashSet<TypeDef>();
        var directories = new HashSet<string>(configuration.ExtraReferenceDirectories)
        {
            configuration.InputDirectory
        };
        
        foreach (var path in directories.SelectMany(p => Directory.EnumerateFiles(p, "*.dll")))
        {
            if (assemblyResolver.TryLoadAssembly(path, moduleContext) is not { } assemblyDef) continue;
            foreach (var typeDef in assemblyDef.Modules.SelectMany(m => m.GetTypes()))
            {
                typeDefs.Add(typeDef);
            }
        }

        return typeDefs;
    }

    private HashSet<AssemblyDef> LoadAllowedAssemblies(TrimmerConfiguration configuration)
    {
        var inputAssemblies = Directory.EnumerateFiles(configuration.InputDirectory, "*.dll")
            .Select(p => assemblyResolver.Resolve(p))
            .OfNotNull()
            .ToList();

        return configuration.TrimMode switch
        {
            TrimMode.Include => inputAssemblies
                .Where(a => configuration.TrimAssemblies.Contains(a.Name))
                .ToHashSet(),
            TrimMode.Exclude => inputAssemblies
                .Where(a => !configuration.ExcludeAssemblies.Contains(a.Name))
                .ToHashSet(),
            _ => throw new ArgumentOutOfRangeException()
        };
    }
}
