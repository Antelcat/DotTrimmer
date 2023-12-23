using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Antelcat.Parameterization;
using dnlib.DotNet;

namespace Antelcat.Trimmer;

[Parameterization]
public static partial class Program
{
    public static async Task Main(string[] args)
    {
        await ExecuteArgumentsAsync(args);
    }

    private class SimpleAssemblyResolver : IAssemblyResolver
    {
        public IEnumerable<AssemblyDef> AssemblyDefs => assemblyCache.Values;
        
        private readonly Dictionary<string, AssemblyDef> assemblyCache = new();
        private readonly Dictionary<string, string> pathMap = new();
        
        public bool TryLoadAssembly(string path, [NotNullWhen(true)] out AssemblyDef? assemblyDef)
        {
            try
            {
                assemblyDef = AssemblyDef.Load(File.ReadAllBytes(path), ModuleContext);
                
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
                return true;
            }
            catch
            {
                assemblyDef = null;
                return false;
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
            return pathMap.GetValueOrDefault(fullPath) is { } name ? assemblyCache.GetValueOrDefault(name) : null;
        }
        
        public string? ResolveFullPath(IFullName assembly)
        {
            return pathMap.FirstOrDefault(p => p.Value == assembly.Name).Key;
        }
    }

    private readonly static SimpleAssemblyResolver AssemblyResolver = new();
    private readonly static ModuleContext ModuleContext = new(AssemblyResolver);

    private static string NormalizePath(string path)
    {
        return Environment.ExpandEnvironmentVariables(Path.GetFullPath(path)).TrimEnd('\\', '/');
    }

    [Command]
    private static async ValueTask Trim(
        string[] includeDirectory,
        string[] allowedAssembly,
        string[] rootType,
        string outputPath = "./output")
    {
        var typeDefs = new HashSet<TypeDef>();
        foreach (var path in includeDirectory.SelectMany(p => Directory.EnumerateFiles(p, "*.dll")))
        {
            if (!AssemblyResolver.TryLoadAssembly(NormalizePath(path), out var assemblyDef)) continue;
            foreach (var typeDef in assemblyDef.Modules.SelectMany(m => m.GetTypes()))
            {
                typeDefs.Add(typeDef);
            }
        }


        // 对于assemblyDef中的每一个类型，如果他没有被任何其他类型引用，那么就删除他
        // 算法需要构建多个有向图，考虑泛型等因素
        // 遍历每一个有向图，如果其中的任意一个节点位于rootTypes中，那这个图中的所有节点都会标记为保留
        // 之后，重新遍历每一个有向图，如果其中的任意一个节点被标记为保留，那么这个图中的所有节点都会标记为保留
        // 不断重复这个过程，直到不再有新的节点被标记为保留
        // 之后，删除所有没有被标记为保留、且位于allowedTypeDefs中的节点
        var typeGraph = new TypeGraph();
        using (var progressBar = new ProgressBar("Building type graph...", typeDefs.Count))
        {
            // foreach (var typeDef in typeDefs)
            // {
            //     typeGraph.TryAdd(typeDef, ResolveDependencies(typeDef));
            //     progressBar.Current++;
            // }
            await Parallel.ForEachAsync(typeDefs,
                (typeDef, _) =>
                {
                    typeGraph.TryAdd(typeDef, ResolveDependencies(typeDef));
                    progressBar.Current++;
                    return ValueTask.CompletedTask;
                });
        }


        var rootTypeDefs = typeDefs.Where(p => rootType.Contains(p.FullName)).Distinct().SelectMany(ResolveTypeDefOrRef).ToHashSet();
        Console.WriteLine("Preserving types...");
        // TODO: 保留CLR核心类型
        // foreach (var VARIABLE in typeDefs.Where(t => t.cl))
        // {
        //     
        // }

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
                     .SelectMany(ResolveCustomAttribute))
        {
            typeGraph.Preserve(customAttributeTypeDef);
        }


        var allowedAssemblies = allowedAssembly
            .Select(NormalizePath)
            .Select(p => AssemblyResolver.Resolve(p))
            .OfNotNull()
            .ToHashSet();
        Console.WriteLine("Removing unused types...");
        foreach (var typeDef in allowedAssemblies
                     .Select(assemblyDef => typeDefs.Where(typeDef => typeDef.DefinitionAssembly == assemblyDef).ToHashSet())
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


        var typeRefs = allowedAssemblies.SelectMany(assemblyDef => assemblyDef.Modules.SelectMany(m => m.GetTypeRefs())).ToList();
        using (var progressBar = new ProgressBar("Rebuilding forwarded TypeRefs...", typeRefs.Count))
        {
            foreach (var moduleDef in allowedAssemblies.SelectMany(assemblyDef => assemblyDef.Modules))
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


        using (var progressBar = new ProgressBar("Rebuilding metadata...", allowedAssemblies.Count))
        {
            foreach (var assemblyDef in allowedAssemblies.ToList())
            {
                // 写出重新读取，重建元数据
                AssemblyResolver.Remove(assemblyDef);
                allowedAssemblies.Remove(assemblyDef);
                typeDefs.RemoveWhere(typeDef => assemblyDef.Modules.Contains(typeDef.Module));

                using var memoryStream = new MemoryStream();
                assemblyDef.Write(memoryStream);
                memoryStream.Position = 0;
                var newAssemblyDef = AssemblyDef.Load(memoryStream, ModuleContext);
                foreach (var moduleDef in newAssemblyDef.Modules)
                {
                    moduleDef.EnableTypeDefFindCache = true;
                }
                AssemblyResolver.Replace(newAssemblyDef);
                allowedAssemblies.Add(newAssemblyDef);
                foreach (var typeDef in newAssemblyDef.Modules.SelectMany(m => m.GetTypes()))
                {
                    typeDefs.Add(typeDef);
                }

                progressBar.Current++;
            }

            rootTypeDefs = typeDefs.Where(p => rootType.Contains(p.FullName)).Distinct().SelectMany(ResolveTypeDefOrRef).ToHashSet();
        }


        Console.WriteLine("Removing unused assemblies...");
        var assemblyGraph = new AssemblyGraph();
        foreach (var moduleDef in allowedAssemblies.SelectMany(assemblyDef => assemblyDef.Modules))
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

    /// <summary>
    /// 解析一个类型的所有依赖，包括基类、接口、字段、方法、事件、自定义特性、嵌套类型等，深度为1
    /// </summary>
    /// <param name="typeDef"></param>
    /// <returns></returns>
    private static IEnumerable<TypeDef> ResolveDependencies(TypeDef typeDef)
    {
        foreach (var baseTypeDef in ResolveTypeDefOrRef(typeDef.BaseType))
        {
            yield return baseTypeDef;
        }

        if (typeDef.HasInterfaces)
        {
            foreach (var interfaceTypeDef in typeDef.Interfaces.SelectMany(i => ResolveTypeDefOrRef(i.Interface)))
            {
                yield return interfaceTypeDef;
            }
        }

        if (typeDef.HasFields)
        {
            foreach (var field in typeDef.Fields)
            {
                foreach (var fieldTypeDef in ResolveTypeSig(field.FieldType))
                {
                    yield return fieldTypeDef;
                }
                foreach (var fieldAttributeTypeDef in field.CustomAttributes.SelectMany(ResolveCustomAttribute))
                {
                    yield return fieldAttributeTypeDef;
                }
            }
        }

        if (typeDef.HasMethods)
        {
            foreach (var methodDef in typeDef.Methods)
            {
                // 自定义特性
                if (methodDef.HasCustomAttributes)
                {
                    foreach (var methodAttributeTypeDef in methodDef.CustomAttributes.SelectMany(ResolveCustomAttribute))
                    {
                        yield return methodAttributeTypeDef;
                    }
                }

                // 参数类型
                foreach (var parameterTypeDef in methodDef.Parameters.SelectMany(p => ResolveTypeSig(p.Type)))
                {
                    yield return parameterTypeDef;
                }

                // 泛型参数自定义特性
                if (methodDef.HasGenericParameters)
                {
                    foreach (var genericParamAttributeTypeDef in methodDef.GenericParameters
                                 .SelectMany(gp => gp.CustomAttributes)
                                 .SelectMany(ResolveCustomAttribute))
                    {
                        yield return genericParamAttributeTypeDef;
                    }
                }

                // 参数自定义特性和返回值自定义特性
                if (methodDef.HasParamDefs)
                {
                    foreach (var paramDef in methodDef.ParamDefs)
                    {
                        foreach (var returnAttributeTypeDef in paramDef.CustomAttributes.SelectMany(ResolveCustomAttribute))
                        {
                            yield return returnAttributeTypeDef;
                        }
                    }
                }

                // 返回值类型
                foreach (var returnTypeDef in ResolveTypeSig(methodDef.ReturnType))
                {
                    yield return returnTypeDef;
                }

                // 方法内部引用
                if (!methodDef.HasBody) continue;

                if (methodDef.Body.HasVariables)
                {
                    foreach (var variableTypeDef in methodDef.Body.Variables.SelectMany(v => ResolveTypeSig(v.Type)))
                    {
                        yield return variableTypeDef;
                    }
                }
                
                foreach (var instruction in methodDef.Body.Instructions)
                {
                    switch (instruction.Operand)
                    {
                        case MemberRef memberRef:
                        {
                            foreach (var memberTypeDef in ResolveTypeDefOrRef(memberRef.DeclaringType))
                            {
                                yield return memberTypeDef;
                            }
                            break;
                        }
                        case ITypeDefOrRef typeDefOrRef:
                        {
                            foreach (var instructionTypeDef in ResolveTypeDefOrRef(typeDefOrRef))
                            {
                                yield return instructionTypeDef;
                            }
                            break;
                        }
                        case MethodDef methodDefOperand:
                        {
                            if (methodDefOperand.DeclaringType is { } methodDefOperandTypeDef)
                            {
                                yield return methodDefOperandTypeDef;
                            }
                            break;
                        }
                        case MethodSpec methodSpec:
                        {
                            foreach (var methodSpecTypeDef in ResolveTypeDefOrRef(methodSpec.Method.DeclaringType))
                            {
                                yield return methodSpecTypeDef;
                            }

                            // 这里只处理这个方法调用时的泛型参数，其他的不用处理，因为此时已经引用了方法的所有类，接下来会处理
                            foreach (var methodSpecGenericTypeDef in methodSpec.GenericInstMethodSig.GenericArguments
                                         .SelectMany(t => ResolveTypeDefOrRef(t.ToTypeDefOrRef())))
                            {
                                yield return methodSpecGenericTypeDef;
                            }
                            break;
                        }
                        case FieldDef fieldDefOperand:
                        {
                            if (fieldDefOperand.DeclaringType is { } fieldDefOperandTypeDef)
                            {
                                yield return fieldDefOperandTypeDef;
                            }
                            break;
                        }
                    }
                }

                foreach (var exceptionHandler in methodDef.Body.ExceptionHandlers)
                {
                    foreach (var catchTypeDefTypeDef in ResolveTypeDefOrRef(exceptionHandler.CatchType))
                    {
                        yield return catchTypeDefTypeDef;
                    }
                }
            }
        }

        if (typeDef.HasEvents)
        {
            foreach (var @event in typeDef.Events)
            {
                foreach (var eventTypeDef in ResolveTypeDefOrRef(@event.EventType))
                {
                    yield return eventTypeDef;
                }

                if (!@event.HasCustomAttributes) continue;
                foreach (var fieldAttributeTypeDef in @event.CustomAttributes.SelectMany(ResolveCustomAttribute))
                {
                    yield return fieldAttributeTypeDef;
                }
            }
        }

        if (typeDef.HasCustomAttributes)
        {
            foreach (var attributeTypeDef in typeDef.CustomAttributes.SelectMany(ResolveCustomAttribute))
            {
                yield return attributeTypeDef;
            }
        }

        if (typeDef.HasNestedTypes)
        {
            foreach (var nestedType in typeDef.NestedTypes)
            {
                yield return nestedType;
            }
        }

        if (typeDef.HasGenericParameters)
        {
            foreach (var genericParamAttributeTypeDef in typeDef.GenericParameters
                         .SelectMany(gp => gp.CustomAttributes)
                         .SelectMany(ResolveCustomAttribute))
            {
                yield return genericParamAttributeTypeDef;
            }
        }
    }

    /// <summary>
    /// 提取自定义特性，这包括它自己本身的TypeDef、Enum、以及可能的typeof(T)中的T的TypeDef
    /// </summary>
    /// <param name="customAttribute"></param>
    /// <returns></returns>
    private static IEnumerable<TypeDef> ResolveCustomAttribute(CustomAttribute customAttribute)
    {
        foreach (var typeDef in ResolveTypeDefOrRef(customAttribute.AttributeType))
        {
            yield return typeDef;
        }

        if (customAttribute.HasConstructorArguments)
        {
            foreach (var caArgument in customAttribute.ConstructorArguments)
            {
                foreach (var typeDef in ResolveTypeDefOrRef(caArgument.Type.ToTypeDefOrRef())) // Enum
                {
                    yield return typeDef;
                }
                if (caArgument.Value is TypeSig typeSig)
                {
                    foreach (var typeSigTypeDef in ResolveTypeDefOrRef(typeSig.ToTypeDefOrRef())) // typeof
                    {
                        yield return typeSigTypeDef;
                    }
                }
            }
        }

        if (customAttribute.HasNamedArguments)
        {
            foreach (var caNamedArgument in customAttribute.NamedArguments)
            {
                foreach (var typeDef in ResolveTypeDefOrRef(caNamedArgument.Type.ToTypeDefOrRef())) // Enum
                {
                    yield return typeDef;
                }
                if (caNamedArgument.Value is TypeSig typeSig)
                {
                    foreach (var typeSigTypeDef in ResolveTypeDefOrRef(typeSig.ToTypeDefOrRef())) // typeof
                    {
                        yield return typeSigTypeDef;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 提取所有的泛型（如果有）以及泛型参数的类型（递归），同样也会提取泛型参数的自定义特性
    /// </summary>
    /// <param name="typeDefOrRef"></param>
    /// <returns></returns>
    private static IEnumerable<TypeDef> ResolveTypeDefOrRef(ITypeDefOrRef? typeDefOrRef)
    {
        while (typeDefOrRef != null)
        {
            switch (typeDefOrRef)
            {
                case TypeDef typeDef:
                {
                    yield return typeDef;
                    break;
                }
                case TypeRef typeRef:
                {
                    if (typeRef.Resolve() is not { } typeDef)
                    {
                        Console.Error.WriteLine($"Cannot resolve type {typeDefOrRef.AssemblyQualifiedName}");
                        yield break;
                    }

                    yield return typeDef;
                    break;
                }
                case TypeSpec typeSpec: // 泛型实例
                {
                    if (typeSpec.TryGetGenericInstSig() is { } genericInst)
                    {
                        // 返回泛型实例的基类型
                        foreach (var typeDef in ResolveTypeDefOrRef(genericInst.GenericType.TypeDefOrRef))
                        {
                            yield return typeDef;
                        }

                        // 遍历并返回所有泛型参数的类型
                        foreach (var argTypeSig in genericInst.GenericArguments)
                        {
                            if (argTypeSig.ToTypeDefOrRef() is not { } argType) continue;

                            // 泛型参数的类型
                            foreach (var typeDef in ResolveTypeDefOrRef(argType))
                            {
                                yield return typeDef;
                            }
                            // 泛型参数的自定义特性
                            foreach (var argAttributeTypeDef in argType.CustomAttributes.SelectMany(ResolveCustomAttribute))
                            {
                                yield return argAttributeTypeDef;
                            }
                        }
                    }

                    if (typeSpec.HasCustomAttributes)
                    {
                        foreach (var typeSpecAttributeTypeDef in typeSpec.CustomAttributes.SelectMany(ResolveCustomAttribute))
                        {
                            yield return typeSpecAttributeTypeDef;
                        }
                    }
                    break;
                }
                default:
                {
                    if (typeDefOrRef.ScopeType == null)
                    {
                        Console.Error.WriteLine($"Cannot resolve type {typeDefOrRef.AssemblyQualifiedName}");
                        yield break;
                    }

                    typeDefOrRef = typeDefOrRef.ScopeType;
                    continue;
                }
            }
            break;
        }
    }

    private static IEnumerable<TypeDef> ResolveTypeSig(TypeSig typeSig)
    {
        while (true)
        {
            switch (typeSig)
            {
                case TypeDefOrRefSig typeDefOrRefSig:
                {
                    foreach (var typeDef in ResolveTypeDefOrRef(typeDefOrRefSig.TypeDefOrRef))
                    {
                        yield return typeDef;
                    }
                    break;
                }
                case ModifierSig modifierSig:
                {
                    foreach (var modifierTypeDef in ResolveTypeDefOrRef(modifierSig.Modifier))
                    {
                        yield return modifierTypeDef;
                    }
                    break;
                }
                case GenericInstSig genericInstSig:
                {
                    foreach (var arg in genericInstSig.GenericArguments)
                    {
                        foreach (var argType in ResolveTypeSig(arg))
                        {
                            yield return argType;
                        }
                    }
                    break;
                }
                case FnPtrSig fnPtrSig:
                {
                    if (fnPtrSig.Signature is MethodSig methodSig)
                    {
                        foreach (var param in methodSig.Params)
                        {
                            foreach (var paramTypeDef in ResolveTypeSig(param))
                            {
                                yield return paramTypeDef;
                            }
                        }
                        if (methodSig.RetType != null)
                        {
                            typeSig = methodSig.RetType;
                            continue;
                        }
                    }
                    break;
                }
                case ArraySigBase:
                case PtrSig:
                case ByRefSig:
                case PinnedSig:
                {
                    typeSig = typeSig.Next;
                    continue;
                }
                case GenericSig:
                {
                    break;
                }
                default:
                {
                    Debugger.Break();
                    break;
                }
            }
            break;
        }
    }

    private class ProgressBar : IDisposable
    {
        public int Current
        {
            get => current;
            set
            {
                current = value;
                progress = current * 100 / total;
            }
        }

        private int current, progress;
        private readonly string prompt;
        private readonly int total;
        private readonly CancellationTokenSource cancellationTokenSource = new();

        public ProgressBar(string prompt, int total)
        {
            this.prompt = prompt;
            this.total = total;
            Task.Factory.StartNew(UpdateTask, cancellationTokenSource.Token, TaskCreationOptions.LongRunning);
        }

        private async ValueTask UpdateTask(object? arg)
        {
            var cancellationToken = (CancellationToken)arg!;
            var progressStrings = new[]
            {
                " - ",
                " \\ ",
                " | ",
                " / "
            };
            var progressStringIndex = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                Console.CursorVisible = false;
                Console.CursorLeft = 0;
                Console.Write(progressStrings[progressStringIndex++]);
                if (progressStringIndex == progressStrings.Length) progressStringIndex = 0;
                Console.Write(prompt);
                Console.Write(progress);
                Console.Write('%');

                await Task.Delay(100, cancellationToken);
            }
        }

        public void Dispose()
        {
            cancellationTokenSource.Cancel();
            Console.CursorLeft = 0;
            Console.WriteLine($"{prompt}OK!    ");
            Console.CursorVisible = true;
        }
    }
    
    private static IEnumerable<T> OfNotNull<T>(this IEnumerable<T?> source) where T : class
    {
        foreach (var item in source)
        {
            if (item != null)
            {
                yield return item;
            }
        }
    }
}

internal abstract class DependencyGraph<TKey, TType> where TKey : class where TType : class
{
    protected class DependencyNode(TType type, IEnumerable<TType> dependencies)
    {
        public TType Type { get; } = type;
        public HashSet<TType> Dependencies { get; } = [..dependencies];
        public bool IsPreserved { get; set; }

        public override bool Equals(object? obj) => obj is DependencyNode other && Type.Equals(other.Type);
        public override int GetHashCode() => Type.GetHashCode();
    }

    private readonly ConcurrentDictionary<TKey, DependencyNode> nodes = new();
    
    protected abstract TKey GetKey(TType type);

    public bool TryAdd(TType type, IEnumerable<TType> dependencies)
    {
        return nodes.TryAdd(GetKey(type), new DependencyNode(type, dependencies));
    }

    public void Preserve(TType type)
    {
        if (!nodes.TryGetValue(GetKey(type), out var node)) return;
        if (node.IsPreserved) return;
        node.IsPreserved = true;
        foreach (var dependency in node.Dependencies)
        {
            Preserve(dependency);
        }
        
        PreserveNode(node);
    }
    
    protected virtual void PreserveNode(DependencyNode node) { }

    public IEnumerable<TType> EnumerateNodes(bool isPreserved)
    {
        return nodes.Values.Where(node => node.IsPreserved == isPreserved).Select(node => node.Type);
    }
}

internal class TypeGraph : DependencyGraph<TypeDef, TypeDef>
{
    protected override TypeDef GetKey(TypeDef type)
    {
        return type;
    }

    protected override void PreserveNode(DependencyNode node)
    {
        var parentType = node.Type.DeclaringType;
        while (parentType != null)
        {
            Preserve(parentType);
            parentType = parentType.DeclaringType;
        }
    }
}

internal class AssemblyGraph : DependencyGraph<string, AssemblyDef>
{
    protected override string GetKey(AssemblyDef type)
    {
        return type.FullName;
    }
}