using ARKBreedingStats.species;
using ARKBreedingStats.values;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyMcpServer;

internal static class SpeciesNameResolver
{
    private const string GeneratedNamesFileName = "species-names.generated.json";

    private static readonly Lazy<IReadOnlyList<SpeciesNameEntry>> GeneratedNames =
        new(LoadGeneratedNames, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryResolve(
        Values values,
        string input,
        out Species species,
        out IReadOnlyList<string> candidates)
    {
        var trimmedInput = input.Trim();
        if (values.TryGetSpeciesByName(trimmedInput, out species))
        {
            candidates = [species.name];
            return true;
        }

        var query = Normalize(trimmedInput);
        if (query.Length == 0)
        {
            species = null!;
            candidates = [];
            return false;
        }

        var entries = BuildSearchEntries(values);
        var exactMatches = entries
            .Where(entry => entry.NormalizedAlias == query)
            .Select(entry => entry.CanonicalName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (TryResolveSingle(values, exactMatches, out species))
        {
            candidates = exactMatches;
            return true;
        }

        var partialMatches = query.Length >= 2
            ? entries
                .Where(entry => entry.NormalizedAlias.Contains(query, StringComparison.Ordinal)
                    || query.Contains(entry.NormalizedAlias, StringComparison.Ordinal))
                .GroupBy(
                    entry => Math.Abs(entry.NormalizedAlias.Length - query.Length))
                .OrderBy(group => group.Key)
                .FirstOrDefault()?
                .Select(entry => entry.CanonicalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray()
            : null;

        candidates = partialMatches ?? [];
        return TryResolveSingle(values, candidates, out species);
    }

    private static bool TryResolveSingle(
        Values values,
        IReadOnlyList<string> candidates,
        out Species species)
    {
        if (candidates.Count == 1
            && values.TryGetSpeciesByName(candidates[0], out species))
        {
            return true;
        }

        species = null!;
        return false;
    }

    private static SearchEntry[] BuildSearchEntries(Values values)
    {
        var entries = new List<SearchEntry>();
        entries.AddRange(GeneratedNames.Value.SelectMany(entry =>
            entry.LocalizedNames.Values.SelectMany(ReadNames).Select(alias =>
                new SearchEntry(Normalize(alias), entry.Name))));

        foreach (var alias in values.speciesWithAliasesList ?? [])
        {
            if (values.TryGetSpeciesByName(alias, out var resolvedSpecies))
            {
                entries.Add(new SearchEntry(Normalize(alias), resolvedSpecies.name));
            }
        }

        return entries
            .Where(entry => entry.NormalizedAlias.Length > 0)
            .Distinct()
            .ToArray();
    }

    private static IReadOnlyList<SpeciesNameEntry> LoadGeneratedNames()
    {
        var configuredPath = Environment.GetEnvironmentVariable("ASB_MCP_SPECIES_NAMES_PATH");
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "Data", GeneratedNamesFileName)
            : configuredPath;

        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<SpeciesNameEntry>>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
    }

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

    private readonly record struct SearchEntry(string NormalizedAlias, string CanonicalName);

    private sealed class SpeciesNameEntry
    {
        public required string Name { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> LocalizedNames { get; init; } = [];
    }
}
