using System.Xml.Serialization;

namespace Antelcat.DotTrimmer.Models;

[Serializable]
[XmlRoot("Root")]
public class RootSettings
{
    public enum PreserveMode
    {
        All = 1,
        None = 2
    }
    
    public abstract class ItemBase
    {
        [XmlAttribute]
        public required string Name { get; init; }
        
        [XmlAttribute("Preserve")]
        public PreserveMode PreserveMode { get; init; }
    }

    public class Type : ItemBase;

    public class Assembly : ItemBase
    {
        [XmlElement("Type")]
        [XmlArray("Types")]
        [XmlArrayItem("Type")]
        public List<Type> Types { get; init; } = new();
    }

    [XmlElement("Assembly")]
    public List<Assembly> Assemblies { get; init; } = new();
}