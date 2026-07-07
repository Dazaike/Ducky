using System.Text.Json.Serialization;

namespace Ducky.Core.Config;

[JsonSerializable(typeof(DuckAppSettings))]
[JsonSerializable(typeof(DuckingSettings))]
[JsonSerializable(typeof(CalibrationData))]
[JsonSerializable(typeof(TargetProfile))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<TargetProfile>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class DuckAppJsonContext : JsonSerializerContext;
