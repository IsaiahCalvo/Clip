using System.Text.Json.Serialization;

namespace Clip.Core;

// System.Text.Json source-generation context for the "Open with" persisted models.
// Using the source-gen contexts (instead of reflection-based JsonSerializer<T> overloads)
// keeps the trimmed Release MSIX from silently dropping the metadata these types need.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, List<OpenWithRecentStore.RecentApp>>))]
[JsonSerializable(typeof(List<PackagedAppDiscovery.StartAppJson>))]
internal partial class OpenWithJsonContext : JsonSerializerContext
{
}
