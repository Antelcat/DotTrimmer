using dnlib.DotNet;

namespace Antelcat.DotTrimmer.Models;

/// <summary>
/// 记录程序集依赖关系的图
/// </summary>
internal class AssemblyGraph : DependencyGraph<string, AssemblyDef>
{
    protected override string GetKey(AssemblyDef type)
    {
        return type.FullName;
    }
}