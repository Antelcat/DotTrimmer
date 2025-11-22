using System.Diagnostics;
using dnlib.DotNet;

namespace Antelcat.DotTrimmer;

public static class ExtensionMethods
{
    /// <summary>
    /// Resolve all dependencies of a type, including base class, interfaces, fields, methods, events, custom attributes, nested types, etc., with a depth of 1
    /// </summary>
    /// <param name="typeDef"></param>
    /// <returns></returns>
    public static IEnumerable<TypeDef> ResolveDependencies(this TypeDef typeDef)
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
                // Custom attributes
                if (methodDef.HasCustomAttributes)
                {
                    foreach (var methodAttributeTypeDef in methodDef.CustomAttributes.SelectMany(ResolveCustomAttribute))
                    {
                        yield return methodAttributeTypeDef;
                    }
                }

                // Parameter types
                foreach (var parameterTypeDef in methodDef.Parameters.SelectMany(p => ResolveTypeSig(p.Type)))
                {
                    yield return parameterTypeDef;
                }

                // Generic parameter custom attributes
                if (methodDef.HasGenericParameters)
                {
                    foreach (var genericParamAttributeTypeDef in methodDef.GenericParameters
                                 .SelectMany(gp => gp.CustomAttributes)
                                 .SelectMany(ResolveCustomAttribute))
                    {
                        yield return genericParamAttributeTypeDef;
                    }
                }

                // Parameter custom attributes and return value custom attributes
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

                // Return value type
                foreach (var returnTypeDef in ResolveTypeSig(methodDef.ReturnType))
                {
                    yield return returnTypeDef;
                }

                // Method body references
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

                            // Only process generic arguments of this method call, others are not processed because all classes referenced by the method have been referenced and will be processed next
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
    /// Extract custom attributes, including its own TypeDef, Enum, and possible TypeDef of T in typeof(T)
    /// </summary>
    /// <param name="customAttribute"></param>
    /// <returns></returns>
    public static IEnumerable<TypeDef> ResolveCustomAttribute(this CustomAttribute customAttribute)
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
    /// Extract all generics (if any) and types of generic parameters (recursive), also extract custom attributes of generic parameters
    /// </summary>
    /// <param name="typeDefOrRef"></param>
    /// <returns></returns>
    public static IEnumerable<TypeDef> ResolveTypeDefOrRef(this ITypeDefOrRef? typeDefOrRef)
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
                case TypeSpec typeSpec: // Generic instance
                {
                    if (typeSpec.TryGetGenericInstSig() is { } genericInst)
                    {
                        // Return base type of generic instance
                        foreach (var typeDef in ResolveTypeDefOrRef(genericInst.GenericType.TypeDefOrRef))
                        {
                            yield return typeDef;
                        }

                        // Traverse and return all types of generic parameters
                        foreach (var argTypeSig in genericInst.GenericArguments)
                        {
                            if (argTypeSig.ToTypeDefOrRef() is not { } argType) continue;

                            // Type of generic parameter
                            foreach (var typeDef in ResolveTypeDefOrRef(argType))
                            {
                                yield return typeDef;
                            }
                            // Custom attributes of generic parameter
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

    public static IEnumerable<TypeDef> ResolveTypeSig(this TypeSig typeSig)
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

    /// <summary>
    /// Return all non-null values
    /// </summary>
    /// <param name="source"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IEnumerable<T> OfNotNull<T>(this IEnumerable<T?> source) where T : class
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