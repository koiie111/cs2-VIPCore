using System.Text.Json;
using System.Text.Json.Serialization;

namespace VIPCore;

public class Config
{
    public float Delay { get; init; } = 2;

    public Dictionary<string, VipGroup> Groups { get; init; } = new()
    {
        {
            "GROUP_NAME", new VipGroup
            {
                Values = new Dictionary<string, object>()
            }
        }
    };
}

public class VipGroup
{
    public Dictionary<string, object> Values { get; init; } = new();

    public bool HasFeature(string key)
    {
        if (!Values.TryGetValue(key, out var value)) return false;
        // vip.json values arrive as JsonElement. Numbers and objects need no string conversion.
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } json => !json.ValueEquals(""),
            JsonElement json => json.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined),
            _ => !string.IsNullOrEmpty(value?.ToString())
        };
    }
}

public class CoreConfig
{
    public int TimeMode { get; init; } = 0;
    public int ServerId { get; init; } = 0;
    public bool UseCenterHtmlMenu { get; init; } = true;
    [JsonPropertyName("ServerIP")] public string ServerIp { get; init; } = "0.0.0.0";
    public int ServerPort { get; init; } = 27015;

    //public bool DisplayUnavailableOptions { get; init; }
    public bool ReOpenMenuAfterItemClick { get; init; } = false;
    public bool VipLogging { get; init; } = true;

    public VipDb Connection { get; init; } = new()
    {
        Host = "HOST",
        Database = "DATABASENAME",
        User = "USER",
        Password = "PASSWORD",
        Port = 3306
    };
}

public class VipDb
{
    public required string Host { get; init; }
    public required string Database { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
    public int Port { get; init; }
}