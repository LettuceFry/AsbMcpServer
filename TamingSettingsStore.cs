using System.IO;
using System.Text.Json;

namespace MyMcpServer;

internal static class TamingSettingsStore
{
    private static readonly object SettingsLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static TamingSettings? _settings;

    public static TamingSettings Get()
    {
        lock (SettingsLock)
        {
            return _settings ??= Load();
        }
    }

    public static TamingSettings GetForCalculation(
        double? tamingSpeedMultiplier,
        bool? useSanguineElixir,
        double? crossbowDamagePercent,
        double? rifleDamagePercent)
    {
        if (tamingSpeedMultiplier.HasValue)
        {
            ValidateTamingSpeedMultiplier(
                tamingSpeedMultiplier.Value,
                nameof(tamingSpeedMultiplier));
        }
        if (crossbowDamagePercent.HasValue)
        {
            ValidateDamagePercent(
                crossbowDamagePercent.Value,
                nameof(crossbowDamagePercent));
        }
        if (rifleDamagePercent.HasValue)
        {
            ValidateDamagePercent(
                rifleDamagePercent.Value,
                nameof(rifleDamagePercent));
        }

        var saved = Get();
        return new TamingSettings
        {
            TamingSpeedMultiplier = tamingSpeedMultiplier ?? saved.TamingSpeedMultiplier,
            UseSanguineElixir = useSanguineElixir ?? saved.UseSanguineElixir,
            CrossbowDamagePercent = crossbowDamagePercent ?? saved.CrossbowDamagePercent,
            RifleDamagePercent = rifleDamagePercent ?? saved.RifleDamagePercent
        };
    }

    public static TamingSettings Update(
        double? tamingSpeedMultiplier,
        bool? useSanguineElixir,
        double? crossbowDamagePercent,
        double? rifleDamagePercent)
    {
        if (!tamingSpeedMultiplier.HasValue
            && !useSanguineElixir.HasValue
            && !crossbowDamagePercent.HasValue
            && !rifleDamagePercent.HasValue)
        {
            throw new ArgumentException("更新する保存設定を1つ以上指定してください。");
        }

        if (tamingSpeedMultiplier.HasValue)
        {
            ValidateTamingSpeedMultiplier(
                tamingSpeedMultiplier.Value,
                nameof(tamingSpeedMultiplier));
        }
        if (crossbowDamagePercent.HasValue)
        {
            ValidateDamagePercent(
                crossbowDamagePercent.Value,
                nameof(crossbowDamagePercent));
        }
        if (rifleDamagePercent.HasValue)
        {
            ValidateDamagePercent(
                rifleDamagePercent.Value,
                nameof(rifleDamagePercent));
        }

        lock (SettingsLock)
        {
            var current = _settings ??= Load();
            var settings = new TamingSettings
            {
                TamingSpeedMultiplier = tamingSpeedMultiplier ?? current.TamingSpeedMultiplier,
                UseSanguineElixir = useSanguineElixir ?? current.UseSanguineElixir,
                CrossbowDamagePercent = crossbowDamagePercent ?? current.CrossbowDamagePercent,
                RifleDamagePercent = rifleDamagePercent ?? current.RifleDamagePercent
            };
            Save(settings);
            _settings = settings;
            return settings;
        }
    }

    private static TamingSettings Load()
    {
        var settingsPath = GetSettingsPath();
        if (!File.Exists(settingsPath))
        {
            return new TamingSettings();
        }

        using var stream = File.OpenRead(settingsPath);
        var settings = JsonSerializer.Deserialize<TamingSettings>(stream, JsonOptions)
            ?? throw new InvalidDataException($"設定ファイル '{settingsPath}' を読み込めませんでした。");

        if (!double.IsFinite(settings.TamingSpeedMultiplier)
            || settings.TamingSpeedMultiplier <= 0)
        {
            throw new InvalidDataException(
                $"設定ファイル '{settingsPath}' のtamingSpeedMultiplierが不正です。");
        }

        ValidateLoadedDamagePercent(
            settings.CrossbowDamagePercent,
            "crossbowDamagePercent",
            settingsPath);
        ValidateLoadedDamagePercent(
            settings.RifleDamagePercent,
            "rifleDamagePercent",
            settingsPath);

        return settings;
    }

    private static void ValidateDamagePercent(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "武器ダメージ率は0より大きい有限値を指定してください。100が標準値です。");
        }
    }

    private static void ValidateTamingSpeedMultiplier(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "テイム速度倍率は0より大きい有限値を指定してください。");
        }
    }

    private static void ValidateLoadedDamagePercent(
        double value,
        string propertyName,
        string settingsPath)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new InvalidDataException(
                $"設定ファイル '{settingsPath}' の{propertyName}が不正です。");
        }
    }

    private static void Save(TamingSettings settings)
    {
        var settingsPath = GetSettingsPath();
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("設定ファイルの保存先を解決できません。");
        Directory.CreateDirectory(directory);

        var temporaryPath = settingsPath + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
            {
                JsonSerializer.Serialize(stream, settings, JsonOptions);
            }

            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetSettingsPath()
    {
        var overriddenPath = Environment.GetEnvironmentVariable("ASB_MCP_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(overriddenPath))
        {
            return Path.GetFullPath(overriddenPath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AsbMcpServer",
            "settings.json");
    }
}

internal sealed class TamingSettings
{
    public double TamingSpeedMultiplier { get; init; } = 1;
    public bool UseSanguineElixir { get; init; }
    public double CrossbowDamagePercent { get; init; } = 100;
    public double RifleDamagePercent { get; init; } = 100;
}
