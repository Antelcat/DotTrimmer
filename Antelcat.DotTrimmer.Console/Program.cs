using System.Diagnostics;
using System.Text;
using System.Xml.Serialization;
using Antelcat.DotTrimmer.Models;
using Antelcat.Parameterization;
using dnlib.DotNet;

namespace Antelcat.DotTrimmer;

[Parameterization]
public static partial class Program
{
    private readonly static SimpleAssemblyResolver AssemblyResolver = new();
    private readonly static ModuleContext ModuleContext = new(AssemblyResolver);

    public static async Task Main(string[] args)
    {
        await ExecuteArgumentsAsync(args);
    }

    private static RootSettings ReadRootSettings(string rootXmlPath)
    {
        return new XmlSerializer(typeof(RootSettings)).Deserialize(File.OpenRead(rootXmlPath)) as RootSettings ??
               throw new InvalidOperationException("Cannot deserialize root settings");
    }

    private static HashSet<TypeDef> LoadTypeDefs(string[] includeDirectories)
    {
        var typeDefs = new HashSet<TypeDef>();
        foreach (var path in includeDirectories.SelectMany(p => Directory.EnumerateFiles(p, "*.dll")))
        {
            if (!AssemblyResolver.TryLoadAssembly(path, ModuleContext, out var assemblyDef)) continue;
            foreach (var typeDef in assemblyDef.Modules.SelectMany(m => m.GetTypes()))
            {
                typeDefs.Add(typeDef);
            }
        }

        return typeDefs;
    }

    private static HashSet<AssemblyDef> LoadAllowedAssemblies(string[] allowedAssemblies) =>
        allowedAssemblies
            .Select(p => AssemblyResolver.Resolve(p))
            .OfNotNull()
            .ToHashSet();

    [Command]
    private static async ValueTask Trim(
        [Argument(FullName = "include-directory", ShortName = 'i')] string[] includeDirectories,
        [Argument(FullName = "allowed-assembly", ShortName = 'a')] string[] allowedAssemblies,
        [Argument(FullName = "root-xml-path", ShortName = 'r')] string rootXmlPath,
        [Argument(FullName = "output-path", ShortName = 'o')] string outputPath = "./output")
    {
        var rootSettings = ReadRootSettings(rootXmlPath);
        var typeDefsSet = LoadTypeDefs(includeDirectories);
        var allowedAssembliesSet = LoadAllowedAssemblies(allowedAssemblies);

        // 对于assemblyDef中的每一个类型，如果他没有被任何其他类型引用，那么就删除他
        // 算法需要构建多个有向图，考虑泛型等因素
        // 遍历每一个有向图，如果其中的任意一个节点位于rootTypes中，那这个图中的所有节点都会标记为保留
        // 之后，重新遍历每一个有向图，如果其中的任意一个节点被标记为保留，那么这个图中的所有节点都会标记为保留
        // 不断重复这个过程，直到不再有新的节点被标记为保留
        // 之后，删除所有没有被标记为保留、且位于allowedTypeDefs中的节点
        var typeGraph = new TypeGraph();
        using (var progressBar = new ProgressBar("Building type graph...", typeDefsSet.Count))
        {
            // foreach (var typeDef in typeDefs)
            // {
            //     typeGraph.TryAdd(typeDef, ResolveDependencies(typeDef));
            //     progressBar.Current++;
            // }
            await Parallel.ForEachAsync(typeDefsSet,
                (typeDef, _) =>
                {
                    typeGraph.TryAdd(typeDef, typeDef.ResolveDependencies());
                    progressBar.Current++;
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
        Console.WriteLine("Preserving types...");
        // 保留CLR核心类型
        foreach (var typeDef in typeDefsSet.Where(t => t.IsRuntimeSpecialName))
        {
            typeGraph.Preserve(typeDef);
        }

        // 保留RootTypes引用的类型
        foreach (var rootTypeDef in rootTypeDefs)
        {
            typeGraph.Preserve(rootTypeDef);
        }

        // 保留被程序集和模块中的自定义特性引用的类型
        foreach (var customAttributeTypeDef in AssemblyResolver.AssemblyDefs
                     .SelectMany(ad => ad.CustomAttributes)
                     .Concat(AssemblyResolver.AssemblyDefs
                         .SelectMany(ad => ad.Modules)
                         .SelectMany(moduleDef => moduleDef.CustomAttributes))
                     .SelectMany(ca => ca.ResolveCustomAttribute()))
        {
            typeGraph.Preserve(customAttributeTypeDef);
        }


        Console.WriteLine("Removing unused types...");
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
        using (var progressBar = new ProgressBar("Rebuilding forwarded TypeRefs...", typeRefs.Count))
        {
            foreach (var moduleDef in allowedAssembliesSet.SelectMany(assemblyDef => assemblyDef.Modules))
            {
                moduleDef.EnableTypeDefFindCache = false; // 关闭缓存，以便修改
            }

            var typeForwardCache = new Dictionary<AssemblyDef, Dictionary<string, IAssembly>>();
            foreach (var typeRef in typeRefs)
            {
                var assemblyRef = typeRef.Scope as IAssembly;
                while (assemblyRef != null) // 处理多次转发的情况
                {
                    if (assemblyRef.IsCorLib() || // 例如netstandard转发了很多类型，不用处理
                        AssemblyResolver.Resolve(assemblyRef) is not { } assemblyDef)
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
                                if (AssemblyResolver.Resolve(exportedType.DefinitionAssembly) == null) continue;
                                cache.Add(exportedType.FullName, exportedType.DefinitionAssembly);
                            }
                        }
                        typeForwardCache.Add(assemblyDef, cache);
                    }

                    cache.TryGetValue(typeRef.FullName, out assemblyRef);
                }

                progressBar.Current++;

                if (assemblyRef == null) continue;
                if (assemblyRef == typeRef.DefinitionAssembly) continue;
                Debug.Assert(assemblyRef.FullName != typeRef.DefinitionAssembly.FullName);
                // Console.WriteLine($"Redirecting \"{typeRef.FullName}\" from \"{typeRef.DefinitionAssembly.Name}\" to \"{assemblyRef.Name}\"...");
                typeRef.ResolutionScope = new AssemblyRefUser(assemblyRef);
            }
        }


        using (var progressBar = new ProgressBar("Rebuilding metadata...", allowedAssembliesSet.Count))
        {
            foreach (var assemblyDef in allowedAssembliesSet.ToList())
            {
                // 写出重新读取，重建元数据
                AssemblyResolver.Remove(assemblyDef);
                allowedAssembliesSet.Remove(assemblyDef);
                typeDefsSet.RemoveWhere(typeDef => assemblyDef.Modules.Contains(typeDef.Module));

                using var memoryStream = new MemoryStream();
                assemblyDef.Write(memoryStream);
                memoryStream.Position = 0;
                var newAssemblyDef = AssemblyDef.Load(memoryStream, ModuleContext);
                foreach (var moduleDef in newAssemblyDef.Modules)
                {
                    moduleDef.EnableTypeDefFindCache = true;
                }
                AssemblyResolver.Replace(newAssemblyDef);
                allowedAssembliesSet.Add(newAssemblyDef);
                foreach (var typeDef in newAssemblyDef.Modules.SelectMany(m => m.GetTypes()))
                {
                    typeDefsSet.Add(typeDef);
                }

                progressBar.Current++;
            }

            rootTypeDefs = ResolveRootTypeDefs().ToHashSet();
        }


        Console.WriteLine("Removing unused assemblies...");
        var assemblyGraph = new AssemblyGraph();
        foreach (var moduleDef in allowedAssembliesSet.SelectMany(assemblyDef => assemblyDef.Modules))
        {
            assemblyGraph.TryAdd(
                moduleDef.Assembly,
                moduleDef.GetAssemblyRefs()
                    .Select(assemblyRef => AssemblyResolver.Resolve(assemblyRef))
                    .OfNotNull());
        }
        foreach (var rootTypeDef in rootTypeDefs)
        {
            assemblyGraph.Preserve(rootTypeDef.Module.Assembly);
        }
        foreach (var rootTypeRef in rootTypeDefs.SelectMany(typeDef => typeDef.Module.GetTypeRefs()))
        {
            if (AssemblyResolver.Resolve(rootTypeRef.DefinitionAssembly) is { } assemblyDef)
            {
                assemblyGraph.Preserve(assemblyDef);
            }
        }
        foreach (var assemblyDef in assemblyGraph.EnumerateNodes(false))
        {
            Console.WriteLine($"Assembly \"{assemblyDef.FullName}\" is no longer used");
        }


        var trimmedAssemblies = assemblyGraph.EnumerateNodes(true).ToList();
        using (var progressBar = new ProgressBar("Writing trimmed assemblies...", trimmedAssemblies.Count))
        {
            outputPath = NormalizePath(outputPath);
            Directory.CreateDirectory(outputPath);
            foreach (var assemblyDef in trimmedAssemblies)
            {
                var assemblyInputPath = AssemblyResolver.ResolveFullPath(assemblyDef) ??
                                        throw new Exception($"Cannot resolve full path of assembly: {assemblyDef.FullName}");
                var assemblyOutputPath = Path.Combine(outputPath, Path.GetFileName(assemblyInputPath));
                try
                {
                    assemblyDef.Write(assemblyOutputPath);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(new Exception($"Error when writing trimmed assembly: {assemblyDef.FullName}", e));
                }

                progressBar.Current++;
            }
        }

        Console.WriteLine("Done.");
    }

    [Command]
    private static async ValueTask Confuse(
        [Argument(FullName = "include-directory", ShortName = 'i')] string[] includeDirectories,
        [Argument(FullName = "allowed-assembly", ShortName = 'a')] string[] allowedAssemblies,
        [Argument(FullName = "root-xml-path", ShortName = 'r')] string rootXmlPath,
        [Argument(FullName = "output-path", ShortName = 'o')] string outputPath = "./output")
    {
        var rootSettings = ReadRootSettings(rootXmlPath);
        var typeDefsSet = LoadTypeDefs(includeDirectories);
        var allowedAssembliesSet = LoadAllowedAssemblies(allowedAssemblies);

        var confusedTypeNameMap = new Dictionary<string, string>();
        var confusedNamespaceMap = new Dictionary<string, string>();
        
        static string GenerateObfuscatedString(int index, int length = 1)
        {
            // 基础Unicode字符，这里使用了基础的CJK统一表意符号区域开始的点，
            // 可以根据需要选择不同的起始点以生成不同样式的乱码字符串
            const int BaseChar = 0x4E00;
            const int EndChar = 0x9FA5;
            
            if (length == 1)
            {
                return ((char)(BaseChar + index % (EndChar - BaseChar))).ToString();
            }

            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                sb.Append((char)(BaseChar + (index * length + i) % (EndChar - BaseChar)));
            }

            return sb.ToString();
        }

    }
}