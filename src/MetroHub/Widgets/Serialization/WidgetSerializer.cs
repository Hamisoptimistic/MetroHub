using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MetroHub.Widgets.Serialization;

/// <summary>
/// Source-generated serialization helper for widget settings payloads.
/// Relies purely on compile-time source generated JsonTypeInfo with zero reflection.
/// </summary>
public static class WidgetSerializer
{
    public static string Serialize<T>(T value) where T : class
    {
        var typeInfo = WidgetJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
        if (typeInfo != null)
        {
            return JsonSerializer.Serialize(value, typeInfo);
        }
        return JsonSerializer.Serialize(value, typeof(T), WidgetJsonContext.Default);
    }

    public static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var typeInfo = WidgetJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>;
            if (typeInfo != null)
            {
                return JsonSerializer.Deserialize(json, typeInfo);
            }
            return (T?)JsonSerializer.Deserialize(json, typeof(T), WidgetJsonContext.Default);
        }
        catch
        {
            return null;
        }
    }
}
