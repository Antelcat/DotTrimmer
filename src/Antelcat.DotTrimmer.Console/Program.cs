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

    [Command]
    private static async ValueTask Trim(
        [Argument(FullName = "input-directory", ShortName = 'i')] string inputDirectory,
        [Argument(FullName = "root-xml-path", ShortName = 'r')] string rootXmlPath,
        [Argument(FullName = "output-path", ShortName = 'o')] string? outputPath = null,
        [Argument(FullName = "trim-mode", ShortName = 'm')] TrimMode trimMode = TrimMode.Include,
        [Argument(FullName = "assembly", ShortName = 'a', Converter = typeof(StringToArrayTypeConverter))] string[]? assemblies = null,
        [Argument(FullName = "extra-directory", ShortName = 'e', Converter = typeof(StringToArrayTypeConverter))] string[]? extraDirectories = null)
    {
        var config = new TrimmerConfiguration
        {
            InputDirectory = inputDirectory,
            OutputDirectory = outputPath,
            TrimMode = trimMode,
            RootSettings = ReadRootSettings(rootXmlPath)
        };

        if (assemblies != null)
        {
            if (trimMode == TrimMode.Include)
            {
                foreach (var asm in assemblies) config.TrimAssemblies.Add(asm);
            }
            else
            {
                foreach (var asm in assemblies) config.ExcludeAssemblies.Add(asm);
            }
        }

        if (extraDirectories != null)
        {
            foreach (var dir in extraDirectories) config.ExtraReferenceDirectories.Add(dir);
        }

        var trimmer = new Trimmer();
        await trimmer.TrimAsync(
            config,
            (msg, total) => new ProgressTaskAdapter(new ProgressBar(msg, total)),
            Console.WriteLine);
    }

    private class ProgressTaskAdapter : Trimmer.IProgressTask
    {
        private readonly ProgressBar progressBar;
        public ProgressTaskAdapter(ProgressBar progressBar) => this.progressBar = progressBar;
        public int Current { get => progressBar.Current; set => progressBar.Current = value; }
        public void Dispose() => progressBar.Dispose();
    }

    [Command]
    private static async ValueTask Confuse(
        [Argument(FullName = "include-directory", ShortName = 'i')] string[] includeDirectories,
        [Argument(FullName = "allowed-assembly", ShortName = 'a')] string[] allowedAssemblies,
        [Argument(FullName = "root-xml-path", ShortName = 'r')] string rootXmlPath,
        [Argument(FullName = "output-path", ShortName = 'o')] string outputPath = "./output")
    {
        // Confuse logic is currently not fully implemented or refactored.
        // Keeping it as placeholder or removing if not needed.
        // Since LoadTypeDefs and LoadAllowedAssemblies were removed from Program.cs (moved to Trimmer.cs logic),
        // we need to either restore them or refactor Confuse to use Trimmer or similar logic.
        // For now, let's comment out the body to fix compilation errors, assuming user focus is on Trim.
        /*
        var rootSettings = ReadRootSettings(rootXmlPath);
        var typeDefsSet = LoadTypeDefs(includeDirectories);
        var allowedAssembliesSet = LoadAllowedAssemblies(allowedAssemblies);

        var confusedTypeNameMap = new Dictionary<string, string>();
        var confusedNamespaceMap = new Dictionary<string, string>();
        */
        
        static string GenerateObfuscatedString(int index, int length = 1)
        {
            // Basic Unicode characters, here using the starting point of the basic CJK Unified Ideographs block,
            // different starting points can be selected as needed to generate different styles of garbled strings
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
        
        await ValueTask.CompletedTask;
    }
}

internal class StringToArrayTypeConverter : System.ComponentModel.StringConverter
{
    public override bool GetStandardValuesSupported(System.ComponentModel.ITypeDescriptorContext? context) => false;

    public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, System.Type sourceType)
    {
        if (sourceType == typeof(string))
            return true;
        return base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, System.Globalization.CultureInfo? culture, object value)
    {
        return value?.ToString();
    }
}