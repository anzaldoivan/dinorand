using System;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace DinoRand.App;

/// <summary>The smallest safe Archipelago player document DinoRand needs to produce.</summary>
public static class PlayerYamlRenderer
{
    public static string Render(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            throw new ArgumentException("Player name cannot be blank.", nameof(playerName));

        // JSON's quoted scalar escapes are valid YAML double-quoted scalar escapes and keep
        // arbitrary names from becoming YAML keys or additional document fields.
        return $"name: {JsonSerializer.Serialize(playerName, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        })}\ngame: Dino Crisis 1\n";
    }
}
