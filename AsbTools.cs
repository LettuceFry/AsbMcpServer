using ARKBreedingStats;
using ARKBreedingStats.Library;
using ARKBreedingStats.mods;
using ARKBreedingStats.species;
using ARKBreedingStats.values;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MyMcpServer;

[McpServerToolType]
public static class AsbTools
{
    private const string AsaValuesFileName = "ASA-values.json";
    private static readonly object ValuesLock = new();
    private static Values? _values;
    private static ServerMultipliers? _officialMultipliers;

    [McpServerTool]
    [Description("指定した生物と野生レベルについて、昏睡に必要な麻酔矢数、餌の必要数、テイム時間、気絶維持用アイテムの必要数と投入基準昏睡値をARK Smart Breedingで計算します。気絶維持用アイテムは種類ごとの代替案であり、合計して使う数ではありません。倍率や武器ダメージ率を指定された単発の計算では、保存設定の取得や更新を先に行わず、このツールへ直接指定してください。省略した設定はツール内部で保存済みの値を使用します。引数で指定した設定は今回の計算だけに適用され、保存済み設定は変更されません。計算後の設定復元は不要です。")]
    public static TamingInfoResult GetTamingInfo(
        [Description("生物名。ARK Smart Breedingの英語名またはエイリアス（例: Rex, Ankylosaurus）")]
        string species,
        [Description("テイム前の野生レベル（1以上）")]
        int level,
        [Description("使用する餌のASB名。省略時は利用可能な餌から最適なものを選び、同性能のキブルは最低等級を優先します（例: Exceptional Kibble, Raw Prime Meat）")]
        string? food = null,
        [Description("今回の計算だけに使うテイム速度倍率。0より大きい値。省略時は保存済み設定を使用し、指定しても保存しません")]
        double? tamingSpeedMultiplier = null,
        [Description("今回の計算だけでサングイン・エリクサーを使用するか。省略時は保存済み設定を使用し、指定しても保存しません")]
        bool? useSanguineElixir = null,
        [Description("今回の計算だけに使うクロスボウの武器ダメージ率。100が標準。省略時は保存済み設定を使用し、指定しても保存しません")]
        double? crossbowDamagePercent = null,
        [Description("今回の計算だけに使うロングネックライフルの武器ダメージ率。100が標準。省略時は保存済み設定を使用し、指定しても保存しません")]
        double? rifleDamagePercent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(species);
        if (level < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(level), "レベルは1以上で指定してください。");
        }

        var settings = TamingSettingsStore.GetForCalculation(
            tamingSpeedMultiplier,
            useSanguineElixir,
            crossbowDamagePercent,
            rifleDamagePercent);
        var temporaryOverrides = new List<string>(4);
        if (tamingSpeedMultiplier.HasValue) temporaryOverrides.Add(nameof(tamingSpeedMultiplier));
        if (useSanguineElixir.HasValue) temporaryOverrides.Add(nameof(useSanguineElixir));
        if (crossbowDamagePercent.HasValue) temporaryOverrides.Add(nameof(crossbowDamagePercent));
        if (rifleDamagePercent.HasValue) temporaryOverrides.Add(nameof(rifleDamagePercent));
        var values = GetValues();
        if (!SpeciesNameResolver.TryResolve(
                values,
                species,
                out Species selectedSpecies,
                out var candidates))
        {
            var candidateText = candidates.Count > 0
                ? $" 候補: {string.Join(", ", candidates)}"
                : string.Empty;
            throw new ArgumentException(
                $"生物 '{species}' を特定できません。日本語名またはASBの英語名を指定してください。{candidateText}",
                nameof(species));
        }

        var tamingData = selectedSpecies.taming;
        if (tamingData is null || (!tamingData.violent && !tamingData.nonViolent))
        {
            throw new InvalidOperationException($"{selectedSpecies.name} はARK Smart Breedingではテイム不可です。");
        }

        var multipliers = (_officialMultipliers
            ?? throw new InvalidOperationException("ARK Smart Breedingの倍率が初期化されていません。"))
            .Copy(withStatMultipliers: false);
        multipliers.TamingSpeedMultiplier = settings.TamingSpeedMultiplier;
        var foodWasSpecified = !string.IsNullOrWhiteSpace(food);
        var (foodName, foodAmount) = foodWasSpecified
            ? FindSpecifiedFood(
                selectedSpecies,
                level,
                multipliers,
                settings.UseSanguineElixir,
                food!)
            : FindBestFood(
                selectedSpecies,
                level,
                multipliers,
                settings.UseSanguineElixir);

        Taming.TamingTimes(
            selectedSpecies,
            level,
            multipliers,
            foodName,
            foodAmount,
            out _,
            out TimeSpan duration,
            out int narcoberryAmount,
            out int ascerbicMushroomAmount,
            out int narcoticAmount,
            out int bioToxinAmount,
            out _,
            out _,
            out _,
            out bool enoughFood,
            useSanguineElixir: settings.UseSanguineElixir);

        if (!enoughFood)
        {
            throw new InvalidOperationException($"{selectedSpecies.name} のテイム計算に必要な餌データが不足しています。");
        }

        int? crossbowArrowCount = null;
        int? tranqDartCount = null;
        int? shockingTranqDartCount = null;
        double? maxTorpor = null;
        IReadOnlyList<TorporMaintenanceOptionResult> torporMaintenanceOptions = [];
        if (tamingData.violent)
        {
            var ammoCounts = GetWeaponAmmoCounts(selectedSpecies, level, multipliers, settings);
            crossbowArrowCount = ammoCounts.CrossbowTranqArrows;
            tranqDartCount = ammoCounts.TranqDarts;
            shockingTranqDartCount = ammoCounts.ShockingTranqDarts;

            var torporStat = selectedSpecies.stats[Stats.Torpidity];
            maxTorpor = torporStat.BaseValue
                * (1 + torporStat.IncPerWildLevel * (level - 1));
            torporMaintenanceOptions = CreateTorporMaintenanceOptions(
                maxTorpor.Value,
                narcoberryAmount,
                ascerbicMushroomAmount,
                narcoticAmount,
                bioToxinAmount);
        }

        return new TamingInfoResult
        {
            Species = selectedSpecies.name,
            Level = level,
            KnockoutRequired = tamingData.violent,
            TranqArrowCrossbowCount = crossbowArrowCount,
            TranqDartCount = tranqDartCount,
            ShockingTranqDartCount = shockingTranqDartCount,
            Food = foodName,
            FoodJapaneseName = FoodNameResolver.GetJapaneseName(foodName),
            FoodAmount = foodAmount,
            TamingSeconds = checked((int)duration.TotalSeconds),
            TamingTime = FormatDuration(duration),
            MaxTorpor = maxTorpor.HasValue ? RoundToTenth(maxTorpor.Value) : null,
            TorporMaintenanceRequired = torporMaintenanceOptions.Any(option => option.RequiredAmount > 0),
            TorporMaintenanceOptions = torporMaintenanceOptions,
            TamingSpeedMultiplier = settings.TamingSpeedMultiplier,
            UseSanguineElixir = settings.UseSanguineElixir,
            CrossbowDamagePercent = settings.CrossbowDamagePercent,
            RifleDamagePercent = settings.RifleDamagePercent,
            TemporaryOverrides = temporaryOverrides,
            PersistentSettingsChanged = false,
            Assumptions = $"ARK Smart Breeding直接計算、テイム速度×{settings.TamingSpeedMultiplier.ToString(CultureInfo.InvariantCulture)}、クロスボウ{settings.CrossbowDamagePercent.ToString(CultureInfo.InvariantCulture)}%、ライフル{settings.RifleDamagePercent.ToString(CultureInfo.InvariantCulture)}%、サングイン・エリクサー{(settings.UseSanguineElixir ? "あり" : "なし")}、一時上書き{(temporaryOverrides.Count > 0 ? string.Join("・", temporaryOverrides) : "なし")}、保存設定変更なし、{(foodWasSpecified ? $"指定餌「{foodName}」" : "Augmented Kibbleと名前がKibbleだけの餌を除くASB既定順の最適餌（同性能のキブルは最低等級を優先）")}"
        };
    }

    [McpServerTool]
    [Description("保存されている既定のテイム速度倍率、サングイン・エリクサー設定、武器ダメージ率を取得します。ユーザーが保存済み設定そのものの確認を求めた場合だけ使用してください。GetTamingInfoの実行前やUpdateSavedSettingsの実行前の確認には使用しないでください。")]
    public static SavedSettingsResult GetSavedSettings()
        => ToSavedSettingsResult(TamingSettingsStore.Get());

    [McpServerTool]
    [Description("今後の計算で使う保存済み既定設定のうち、指定された項目だけを永続更新します。指定しない項目はツール内部で現在値を維持するため、事前にGetSavedSettingsを呼ぶ必要はありません。ユーザーが既定設定の変更を明示的に求めた場合だけ使用してください。1回の計算だけ設定を変える場合は、このツールではなくGetTamingInfoの省略可能引数を使用してください。")]
    public static SavedSettingsResult UpdateSavedSettings(
        [Description("保存するテイム速度倍率。0より大きい値。変更しない場合は省略")]
        double? tamingSpeedMultiplier = null,
        [Description("保存するサングイン・エリクサー使用設定。変更しない場合は省略")]
        bool? useSanguineElixir = null,
        [Description("保存するクロスボウの武器ダメージ率。100が標準。変更しない場合は省略")]
        double? crossbowDamagePercent = null,
        [Description("保存するロングネックライフルの武器ダメージ率。100が標準。変更しない場合は省略")]
        double? rifleDamagePercent = null)
        => ToSavedSettingsResult(TamingSettingsStore.Update(
            tamingSpeedMultiplier,
            useSanguineElixir,
            crossbowDamagePercent,
            rifleDamagePercent));

    [McpServerTool]
    [Description("日本語名、ASBの英語名、別名、または名前の一部から生物名を解決します。曖昧な場合は候補を返します。")]
    public static SpeciesResolutionResult ResolveSpeciesName(
        [Description("検索する生物名。日本語・英語・名前の一部を使用できます（例: ティラノ、アルゲン、Rex）")]
        string species)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(species);
        var resolved = SpeciesNameResolver.TryResolve(
            GetValues(),
            species,
            out var selectedSpecies,
            out var candidates);
        var tamingData = resolved ? selectedSpecies.taming : null;
        var canCalculateTamingInfo = tamingData is not null
            && (tamingData.violent || tamingData.nonViolent)
            && tamingData.eats?.Length > 0;
        var unavailableReason = resolved && !canCalculateTamingInfo
            ? tamingData is null || (!tamingData.violent && !tamingData.nonViolent)
                ? "ASBに対応するテイム計算フラグがありません。"
                : "ASBに餌数と時間の計算に必要なテイム餌データがありません。"
            : null;

        return new SpeciesResolutionResult
        {
            Query = species,
            IsResolved = resolved,
            ResolvedSpecies = resolved ? selectedSpecies.name : null,
            Candidates = candidates,
            CanCalculateTamingInfo = canCalculateTamingInfo,
            UnavailableReason = unavailableReason
        };
    }

    private static SavedSettingsResult ToSavedSettingsResult(TamingSettings settings)
        => new()
        {
            TamingSpeedMultiplier = settings.TamingSpeedMultiplier,
            UseSanguineElixir = settings.UseSanguineElixir,
            CrossbowDamagePercent = settings.CrossbowDamagePercent,
            RifleDamagePercent = settings.RifleDamagePercent
        };

    private static Values GetValues()
    {
        lock (ValuesLock)
        {
            if (_values is not null)
            {
                return _values;
            }

            if (Values.V.modsManifest is null)
            {
                Values.V.modsManifest = LoadLocalModsManifest();
            }

            var values = Values.V.LoadValues(
                forceReload: false,
                out var errorMessage,
                out var errorMessageTitle);

            if (values?.Species?.Any() != true)
            {
                var details = string.IsNullOrWhiteSpace(errorMessage)
                    ? "ASBのvalues.jsonを読み込めませんでした。"
                    : $"{errorMessageTitle}: {errorMessage}";
                throw new InvalidOperationException(details);
            }

            var asaValuesPath = FileService.GetJsonPath(
                FileService.ValuesFolder,
                AsaValuesFileName);
            if (!File.Exists(asaValuesPath))
            {
                throw new FileNotFoundException(
                    "ASBのASA追加データが見つかりません。ASBを更新または再インストールしてください。",
                    asaValuesPath);
            }

            values.LoadModValues(
                [AsaValuesFileName],
                throwExceptionOnFail: true,
                out var loadedMods,
                out var loadAsaResult);
            if (!loadedMods.Any(mod => mod.Id == Ark.Asa))
            {
                throw new InvalidOperationException(
                    $"ASBのASA追加データを読み込めませんでした。{loadAsaResult}".Trim());
            }

            var officialMultipliers = new ServerMultipliers();
            var asaCollection = new CreatureCollection
            {
                serverMultipliers = officialMultipliers
            };
            asaCollection.Game = Ark.Asa;
            values.ApplyMultipliers(asaCollection);

            _officialMultipliers = values.currentServerMultipliers;
            _values = values;
            return values;
        }
    }

    private static ModsManifest LoadLocalModsManifest()
    {
        var manifestPath = FileService.GetJsonPath(
            FileService.ValuesFolder,
            FileService.ManifestFileName);

        if (!FileService.LoadJsonFile(
                manifestPath,
                out ModsManifest manifest,
                out var errorMessage))
        {
            throw new FileNotFoundException(errorMessage, manifestPath);
        }

        foreach (var modInfo in manifest.ModsByFiles.Values)
        {
            if (string.IsNullOrEmpty(modInfo.Format))
            {
                modInfo.Format = manifest.DefaultFormatVersion;
            }
        }

        return manifest;
    }

    private static (string FoodName, int Amount) FindBestFood(
        Species species,
        int level,
        ServerMultipliers multipliers,
        bool useSanguineElixir)
    {
        var taming = species.taming
            ?? throw new InvalidOperationException($"{species.name} にはテイムデータがありません。");
        var foodNames = taming.eats;
        if (foodNames is null || foodNames.Length == 0)
        {
            throw new InvalidOperationException($"{species.name} にはテイム用の餌データがありません。");
        }

        foreach (var foodName in foodNames.Where(name =>
                     !name.Contains("Augmented", StringComparison.OrdinalIgnoreCase)
                     && !name.Trim().Equals("Kibble", StringComparison.OrdinalIgnoreCase)))
        {
            var amount = Taming.FoodAmountNeeded(
                species,
                level,
                multipliers.TamingSpeedMultiplier,
                foodName,
                taming.nonViolent,
                useSanguineElixir);

            if (amount > 0)
            {
                return (FindLowestEquivalentKibble(species, foodName), amount);
            }
        }

        throw new InvalidOperationException($"{species.name} に利用可能なテイム用の餌がありません。");
    }

    private static string FindLowestEquivalentKibble(Species species, string selectedFood)
    {
        string[] kibbleByTier =
        [
            "Basic Kibble", "Simple Kibble", "Regular Kibble",
            "Superior Kibble", "Exceptional Kibble", "Extraordinary Kibble"
        ];
        var selectedTier = Array.IndexOf(kibbleByTier, selectedFood);
        if (selectedTier <= 0) return selectedFood;

        // Match ASB's species-specific override, then default food lookup.
        TamingFood? GetFood(string name)
        {
            if (species.taming.specialFoodValues?.TryGetValue(name, out var food) == true)
                return food;
            return GetValues().defaultFoodData?.GetValueOrDefault(name);
        }

        var selected = GetFood(selectedFood);
        if (selected is null) return selectedFood;

        for (var tier = 0; tier < selectedTier; tier++)
        {
            var name = kibbleByTier[tier];
            if (!species.taming.eats.Contains(name)) continue;
            var candidate = GetFood(name);
            // Equal rounded amounts alone can hide worse affinity/effectiveness.
            // Equal underlying values preserve amount, time and effectiveness.
            if (candidate is not null
                && candidate.affinity == selected.affinity
                && candidate.foodValue == selected.foodValue
                && candidate.quantity == selected.quantity)
            {
                return name;
            }
        }

        return selectedFood;
    }

    private static (string FoodName, int Amount) FindSpecifiedFood(
        Species species,
        int level,
        ServerMultipliers multipliers,
        bool useSanguineElixir,
        string requestedFood)
    {
        var taming = species.taming
            ?? throw new InvalidOperationException($"{species.name} にはテイムデータがありません。");
        var foodNames = (taming.eats ?? [])
            .Where(name => !name.Trim().Equals("Kibble", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (foodNames.Length == 0)
        {
            throw new InvalidOperationException($"{species.name} には指定可能なテイム用の餌データがありません。");
        }

        if (!FoodNameResolver.TryResolve(
                foodNames,
                requestedFood,
                out var foodName,
                out var candidates))
        {
            var candidateNames = candidates.Count > 0 ? candidates : foodNames;
            throw new ArgumentException(
                $"{species.name} の餌 '{requestedFood}' を一意に特定できません。候補: {string.Join(", ", candidateNames)}",
                nameof(requestedFood));
        }

        var amount = Taming.FoodAmountNeeded(
            species,
            level,
            multipliers.TamingSpeedMultiplier,
            foodName,
            taming.nonViolent,
            useSanguineElixir);
        if (amount <= 0)
        {
            throw new InvalidOperationException(
                $"{species.name} を餌 '{foodName}' でテイムするための必要数を計算できません。");
        }

        return (foodName, amount);
    }

    private static WeaponAmmoCounts GetWeaponAmmoCounts(
        Species species,
        int level,
        ServerMultipliers multipliers,
        TamingSettings settings)
    {
        var crossbowInfo = Taming.KnockoutInfo(
            species,
            multipliers,
            level,
            longneck: 0,
            crossbow: settings.CrossbowDamagePercent / 100,
            bow: 0,
            slingshot: 0,
            club: 0,
            prod: 0,
            harpoon: 0,
            boneDamageAdjuster: 1,
            out bool crossbowKnockoutNeeded,
            out _);

        var rifleInfo = Taming.KnockoutInfo(
            species,
            multipliers,
            level,
            longneck: settings.RifleDamagePercent / 100,
            crossbow: 0,
            bow: 0,
            slingshot: 0,
            club: 0,
            prod: 0,
            harpoon: 0,
            boneDamageAdjuster: 1,
            out bool rifleKnockoutNeeded,
            out _);

        if (!crossbowKnockoutNeeded || !rifleKnockoutNeeded)
        {
            throw new InvalidOperationException($"{species.name} は昏睡テイムではありません。");
        }

        var crossbowCounts = ParseAmmoCounts(crossbowInfo, expectedCount: 1);
        var rifleCounts = ParseAmmoCounts(rifleInfo, expectedCount: 2);

        // ASB lists shocking darts before regular tranq darts for the rifle.
        return new WeaponAmmoCounts(
            CrossbowTranqArrows: crossbowCounts[0],
            TranqDarts: rifleCounts[1],
            ShockingTranqDarts: rifleCounts[0]);
    }

    private static int[] ParseAmmoCounts(string knockoutInfo, int expectedCount)
    {
        var matches = Regex.Matches(
            knockoutInfo,
            @"^\s*(\d+)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var counts = matches
            .Select(match => int.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var count)
                ? count
                : -1)
            .ToArray();

        if (counts.Length < expectedCount || counts.Take(expectedCount).Any(count => count < 0))
        {
            throw new InvalidOperationException("ARK Smart Breedingの麻酔武器計算結果を解析できませんでした。");
        }

        return counts;
    }

    private static IReadOnlyList<TorporMaintenanceOptionResult> CreateTorporMaintenanceOptions(
        double maxTorpor,
        int narcoberryAmount,
        int ascerbicMushroomAmount,
        int narcoticAmount,
        int bioToxinAmount)
        =>
        [
            CreateTorporMaintenanceOption("Narcoberry", "ナルコベリー", narcoberryAmount, 7.5, maxTorpor),
            CreateTorporMaintenanceOption("Ascerbic Mushroom", "アスセリック・マッシュルーム", ascerbicMushroomAmount, 25, maxTorpor),
            CreateTorporMaintenanceOption("Narcotic", "麻酔薬", narcoticAmount, 40, maxTorpor),
            CreateTorporMaintenanceOption("Bio Toxin", "バイオトキシン", bioToxinAmount, 80, maxTorpor)
        ];

    private static TorporMaintenanceOptionResult CreateTorporMaintenanceOption(
        string item,
        string itemJapaneseName,
        int requiredAmount,
        double torporPerItem,
        double maxTorpor)
    {
        if (requiredAmount <= 0)
        {
            return new TorporMaintenanceOptionResult
            {
                Item = item,
                ItemJapaneseName = itemJapaneseName,
                RequiredAmount = 0,
                TorporPerItem = torporPerItem,
                CanAdministerAllAtOnce = null,
                AdministerAtOrBelowTorpor = null
            };
        }

        var totalTorporIncrease = requiredAmount * torporPerItem;
        var canAdministerAllAtOnce = totalTorporIncrease < maxTorpor;
        return new TorporMaintenanceOptionResult
        {
            Item = item,
            ItemJapaneseName = itemJapaneseName,
            RequiredAmount = requiredAmount,
            TorporPerItem = torporPerItem,
            CanAdministerAllAtOnce = canAdministerAllAtOnce,
            AdministerAtOrBelowTorpor = canAdministerAllAtOnce
                ? FloorToTenth(maxTorpor - totalTorporIncrease)
                : null
        };
    }

    private static double RoundToTenth(double value)
        => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static double FloorToTenth(double value)
        => Math.Floor(value * 10) / 10;

    private static string FormatDuration(TimeSpan duration)
    {
        var parts = new List<string>(3);
        if (duration.Days > 0) parts.Add($"{duration.Days}日");
        if (duration.Hours > 0) parts.Add($"{duration.Hours}時間");
        if (duration.Minutes > 0) parts.Add($"{duration.Minutes}分");
        if (duration.Seconds > 0 || parts.Count == 0) parts.Add($"{duration.Seconds}秒");
        return string.Join(string.Empty, parts);
    }
}

internal readonly record struct WeaponAmmoCounts(
    int CrossbowTranqArrows,
    int TranqDarts,
    int ShockingTranqDarts);

public sealed class TamingInfoResult
{
    public required string Species { get; init; }
    public required int Level { get; init; }
    public required bool KnockoutRequired { get; init; }
    public int? TranqArrowCrossbowCount { get; init; }
    public int? TranqDartCount { get; init; }
    public int? ShockingTranqDartCount { get; init; }
    public required string Food { get; init; }
    public string? FoodJapaneseName { get; init; }
    public required int FoodAmount { get; init; }
    public required int TamingSeconds { get; init; }
    public required string TamingTime { get; init; }
    public double? MaxTorpor { get; init; }
    public required bool TorporMaintenanceRequired { get; init; }
    public required IReadOnlyList<TorporMaintenanceOptionResult> TorporMaintenanceOptions { get; init; }
    public required double TamingSpeedMultiplier { get; init; }
    public required bool UseSanguineElixir { get; init; }
    public required double CrossbowDamagePercent { get; init; }
    public required double RifleDamagePercent { get; init; }
    public required IReadOnlyList<string> TemporaryOverrides { get; init; }
    public required bool PersistentSettingsChanged { get; init; }
    public required string Assumptions { get; init; }
}

public sealed class TorporMaintenanceOptionResult
{
    public required string Item { get; init; }
    public required string ItemJapaneseName { get; init; }
    public required int RequiredAmount { get; init; }
    public required double TorporPerItem { get; init; }
    public bool? CanAdministerAllAtOnce { get; init; }
    public double? AdministerAtOrBelowTorpor { get; init; }
}

public sealed class SavedSettingsResult
{
    public required double TamingSpeedMultiplier { get; init; }
    public required bool UseSanguineElixir { get; init; }
    public required double CrossbowDamagePercent { get; init; }
    public required double RifleDamagePercent { get; init; }
}

public sealed class SpeciesResolutionResult
{
    public required string Query { get; init; }
    public required bool IsResolved { get; init; }
    public string? ResolvedSpecies { get; init; }
    public required IReadOnlyList<string> Candidates { get; init; }
    public required bool CanCalculateTamingInfo { get; init; }
    public string? UnavailableReason { get; init; }
}
