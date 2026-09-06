using System.IO;
using System.Text.Json;

namespace MyMcpServer;

internal static class AsbInstallationSettings
{
    internal const string FileName = "asb-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string LoadInstallPath()
    {
        var environmentPath = Environment.GetEnvironmentVariable("ASB_INSTALL_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return ValidateInstallPath(environmentPath, "環境変数 ASB_INSTALL_PATH");
        }

        var settingsPath = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException(
                $"ASBの場所が設定されていません。'{settingsPath}' を作成するか、環境変数 ASB_INSTALL_PATH を設定してください。",
                settingsPath);
        }

        using var stream = File.OpenRead(settingsPath);
        var settings = JsonSerializer.Deserialize<SettingsFile>(stream, JsonOptions)
            ?? throw new InvalidDataException($"設定ファイル '{settingsPath}' を読み込めませんでした。");

        if (string.IsNullOrWhiteSpace(settings.AsbInstallPath))
        {
            throw new InvalidDataException(
                $"設定ファイル '{settingsPath}' の asbInstallPath を指定してください。");
        }

        return ValidateInstallPath(settings.AsbInstallPath, $"設定ファイル '{settingsPath}'");
    }

    private static string ValidateInstallPath(string path, string source)
    {
        var fullPath = Path.GetFullPath(
            Environment.ExpandEnvironmentVariables(path.Trim()));
        var asbAssemblyPath = Path.Combine(fullPath, "ARK Smart Breeding.dll");
        if (!File.Exists(asbAssemblyPath))
        {
            throw new DirectoryNotFoundException(
                $"{source} のASBフォルダが正しくありません。'{asbAssemblyPath}' が見つかりません。");
        }

        return fullPath;
    }

    private sealed class SettingsFile
    {
        public string? AsbInstallPath { get; init; }
    }
}
