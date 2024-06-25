using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Antelcat.DotTrimmer.Models;

/// <summary>
/// 表示记录依赖关系的图，子类需要实现 <see cref="GetKey"/> 方法以提供节点的键
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TType"></typeparam>
public abstract class DependencyGraph<TKey, TType> where TKey : class where TType : class
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
    
    public class JsonConverter : JsonConverter<DependencyGraph<TKey, TType>>
    {
        public override DependencyGraph<TKey, TType> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, DependencyGraph<TKey, TType> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var node in value.nodes.Values)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Type");
                JsonSerializer.Serialize(writer, node.Type, options);
                writer.WritePropertyName("Dependencies");
                JsonSerializer.Serialize(writer, node.Dependencies, options);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
    }
}

