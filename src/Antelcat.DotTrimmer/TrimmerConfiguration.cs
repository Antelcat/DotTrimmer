using Antelcat.DotTrimmer.Models;

namespace Antelcat.DotTrimmer;

public enum TrimMode
{
    /// <summary>
    /// Only trim assemblies specified in <see cref="TrimmerConfiguration.TrimAssemblies"/>.
    /// </summary>
    Include,
    
    /// <summary>
    /// Trim all assemblies in <see cref="TrimmerConfiguration.InputDirectory"/> except those in <see cref="TrimmerConfiguration.ExcludeAssemblies"/>.
    /// </summary>
    Exclude
}

public class TrimmerConfiguration
{
    /// <summary>
    /// The directory containing the assemblies to be processed.
    /// </summary>
    public required string InputDirectory { get; set; }

    /// <summary>
    /// The directory to output the trimmed assemblies. If null, defaults to a subdirectory in InputDirectory or overwrite (TBD).
    /// Currently defaulting to "./output" in logic if not handled.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// How to determine which assemblies to trim.
    /// </summary>
    public TrimMode TrimMode { get; set; } = TrimMode.Include;

    /// <summary>
    /// Assemblies to trim (file names without extension or with extension). Used when TrimMode is Include.
    /// </summary>
    public HashSet<string> TrimAssemblies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    
    /// <summary>
    /// Assemblies to exclude from trimming (file names). Used when TrimMode is Exclude.
    /// </summary>
    public HashSet<string> ExcludeAssemblies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extra directories to search for referenced assemblies.
    /// </summary>
    public HashSet<string> ExtraReferenceDirectories { get; set; } = new();

    /// <summary>
    /// Root settings for preservation.
    /// </summary>
    public RootSettings? RootSettings { get; set; }
}
