using dnlib.DotNet;

namespace Antelcat.DotTrimmer.Models;

/// <summary>
/// Graph recording assembly dependencies
/// </summary>
public class AssemblyGraph : DependencyGraph<string, AssemblyDef>
{
    protected override string GetKey(AssemblyDef type)
    {
        return type.FullName;
    }
}