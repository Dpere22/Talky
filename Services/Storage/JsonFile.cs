using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Talky.Models;

namespace Talky.Services.Storage;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<Friend>))]
[JsonSerializable(typeof(List<ChatMessage>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;

/// <summary>Small helpers for reading/writing JSON files atomically.</summary>
internal static class JsonFile
{
    public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Storage] Failed to read {path}: {ex.Message}");
            TryBackupCorruptFile(path);
            return default;
        }
    }

    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, value, typeInfo);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void TryBackupCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
