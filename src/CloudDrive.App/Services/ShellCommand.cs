using System.Text.Json;

namespace CloudDrive.App.Services;

public sealed record ShellCommand(string Command, string? Path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ShellCommand? FromJson(string json) =>
        JsonSerializer.Deserialize<ShellCommand>(json, JsonOptions);
}
