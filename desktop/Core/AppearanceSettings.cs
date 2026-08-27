using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace AichanToolbox.Core;

internal sealed record ThemeSelection(string Id, string ColorScheme, string Background)
{
    public static readonly ThemeSelection Default = new("light", "light", "#eef1f4");

    public bool IsValid =>
        Regex.IsMatch(Id ?? "", @"\A[a-z][a-z0-9-]{0,63}\z") &&
        ColorScheme is "light" or "dark" &&
        Regex.IsMatch(Background ?? "", @"\A#[0-9a-fA-F]{6}\z");

    public Color SurfaceColor => Color.FromRgb(
        Convert.ToByte(Background.Substring(1, 2), 16),
        Convert.ToByte(Background.Substring(3, 2), 16),
        Convert.ToByte(Background.Substring(5, 2), 16));
}

/// <summary>
/// Stores only theme identity and startup colors. Palettes live in the frontend;
/// adding a theme does not require another native switch/case.
/// </summary>
internal sealed class AppearanceSettings
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;

    public ThemeSelection Current { get; private set; }

    public AppearanceSettings(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AichanToolbox", "appearance.json"));
        Current = Read();
    }

    private ThemeSelection Read()
    {
        try
        {
            var selection = JsonSerializer.Deserialize<ThemeSelection>(File.ReadAllText(_path), Json);
            return selection is { IsValid: true } ? selection : ThemeSelection.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ThemeSelection.Default;
        }
    }

    public void Save(ThemeSelection selection)
    {
        if (!selection.IsValid) throw new ArgumentException("无效的主题设置。", nameof(selection));
        if (selection == Current) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(selection, Json));
            File.Move(temporaryPath, _path, true);
            Current = selection;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
