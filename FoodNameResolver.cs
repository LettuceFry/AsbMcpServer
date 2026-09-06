using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyMcpServer;

internal static class FoodNameResolver
{
    private const string FoodNamesFileName = "food-names.json";

    private static readonly Lazy<IReadOnlyList<FoodNameEntry>> Entries =
        new(LoadEntries, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryResolve(
        IReadOnlyList<string> availableFoodNames,
        string input,
        out string foodName,
        out IReadOnlyList<string> candidates)
    {
        var trimmedInput = input.Trim();
        var directMatch = availableFoodNames.FirstOrDefault(name =>
            name.Equals(trimmedInput, StringComparison.OrdinalIgnoreCase));
        if (directMatch is not null)
        {
            foodName = directMatch;
            candidates = [directMatch];
            return true;
        }

        var query = Normalize(trimmedInput);
        if (query.Length == 0)
        {
            foodName = string.Empty;
            candidates = [];
            return false;
        }

        var available = new HashSet<string>(availableFoodNames, StringComparer.OrdinalIgnoreCase);
        var searchEntries = Entries.Value
            .Where(entry => available.Contains(entry.Name))
            .SelectMany(entry => new[] { entry.Name }.Concat(GetLocalizedNames(entry))
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => new SearchEntry(Normalize(alias), entry.Name)))
            .ToArray();

        var exactMatches = searchEntries
            .Where(entry => entry.NormalizedAlias == query)
            .Select(entry => entry.CanonicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (TryResolveSingle(exactMatches, out foodName))
        {
            candidates = exactMatches;
            return true;
        }

        var partialMatches = query.Length >= 2
            ? searchEntries
                .Where(entry => entry.NormalizedAlias.Contains(query, StringComparison.Ordinal)
                    || query.Contains(entry.NormalizedAlias, StringComparison.Ordinal))
                .Select(entry => entry.CanonicalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        candidates = partialMatches;
        return TryResolveSingle(partialMatches, out foodName);
    }

    public static string? GetJapaneseName(string foodName)
    {
        var entry = Entries.Value.FirstOrDefault(entry =>
            entry.Name.Equals(foodName, StringComparison.OrdinalIgnoreCase));
        return entry is not null
            && entry.LocalizedNames.TryGetValue("ja", out var japaneseNames)
                ? ReadNames(japaneseNames).FirstOrDefault()
                : null;
    }

    private static bool TryResolveSingle(
        IReadOnlyList<string> candidates,
        out string foodName)
    {
        if (candidates.Count == 1)
        {
            foodName = candidates[0];
            return true;
        }

        foodName = string.Empty;
        return false;
    }

    private static IReadOnlyList<FoodNameEntry> LoadEntries()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ASB_MCP_FOOD_NAMES_PATH");
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "Data", FoodNamesFileName)
            : configuredPath;

        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<FoodNameEntry>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
    }

    private static IEnumerable<string> GetLocalizedNames(FoodNameEntry entry)
        => entry.LocalizedNames.Values.SelectMany(ReadNames);

    private static IEnumerable<string> ReadNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var name = element.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
            yield break;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                yield return item.GetString()!;
            }
        }
    }

    private static string Normalize(string value)
        => LocalizedNameNormalizer.Normalize(value);

    private sealed class FoodNameEntry
    {
        public required string Name { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> LocalizedNames { get; init; } = [];
    }

    private readonly record struct SearchEntry(string NormalizedAlias, string CanonicalName);
}
