using System.Text.Json.Serialization;
using WeChat_Notifier.Domain;

namespace WeChat_Notifier.Configuration;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppJsonContext : JsonSerializerContext;
