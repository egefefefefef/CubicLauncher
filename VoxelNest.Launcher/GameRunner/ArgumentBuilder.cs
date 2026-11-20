using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoxelNest.Launcher.Installation;
using VoxelNest.Launcher.VersionManager;

namespace VoxelNest.Launcher.GameRunner;

public static partial class ArgumentBuilder
{
    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex VariablePattern();

    public static string Expand(string argument, IReadOnlyDictionary<string, string> variables) => VariablePattern().Replace(argument,
        match => variables.TryGetValue(match.Groups[1].Value, out var value) ? value : throw new InvalidDataException($"Unknown Minecraft argument: {match.Value}"));

    public static IEnumerable<string> Read(IEnumerable<JsonElement>? arguments, MinecraftPlatform platform, IReadOnlyDictionary<string, bool> features)
    {
        if (arguments is null) yield break;
        foreach (var element in arguments)
        {
            if (element.ValueKind == JsonValueKind.String) { yield return element.GetString()!; continue; }
            if (element.TryGetProperty("rules", out var rules) && !RuleEvaluator.Allows(rules.Deserialize<List<PlatformRule>>(new JsonSerializerOptions(JsonSerializerDefaults.Web)), platform, features)) continue;
            var value = element.GetProperty("value");
            if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
            else foreach (var item in value.EnumerateArray()) yield return item.GetString()!;
        }
    }

    public static IReadOnlyList<string> Tokenize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var tokens = new List<string>();
        var buffer = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\\' && i + 1 < input.Length && input[i + 1] is '"' or '\'' && (quote == '\0' || input[i + 1] == quote))
                { buffer.Append(input[++i]); started = true; continue; }
            if (c is '"' or '\'' && (quote == '\0' || quote == c))
                { quote = quote == '\0' ? c : '\0'; started = true; continue; }
            if (char.IsWhiteSpace(c) && quote == '\0')
            {
                if (started) tokens.Add(buffer.ToString());
                buffer.Clear(); started = false; continue;
            }
            started = true;
            buffer.Append(c);
        }
        if (quote != '\0') throw new FormatException("An argument contains an unclosed quote.");
        if (started) tokens.Add(buffer.ToString());
        return tokens;
    }
}
