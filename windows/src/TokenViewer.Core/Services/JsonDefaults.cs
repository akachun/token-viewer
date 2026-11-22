using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenViewer.Core.Services;

public static class JsonDefaults
{
    /// <summary>Options for files the app writes itself (settings, state, credentials).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
