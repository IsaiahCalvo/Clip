using System.Text.Json.Serialization;

namespace Clip.CommandPalette;

[JsonSerializable(typeof(string))]
internal partial class ClipCommandPaletteJsonContext : JsonSerializerContext
{
}
