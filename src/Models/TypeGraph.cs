using dnlib.DotNet;

namespace Antelcat.DotTrimmer.Models;

/// <summary>
/// 记录类型依赖关系的图
/// </summary>
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