using System.Text.Json;
using Windows.Graphics;

namespace AITranslator.Services;

public sealed class WindowPlacementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public WindowPlacementStore(AppPaths paths)
    {
        _filePath = paths.WindowPlacementFile;
    }

    public PointInt32? Load()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<WindowPlacementState>(File.ReadAllText(_filePath), JsonOptions);
            return state is null ? null : new PointInt32(state.X, state.Y);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(PointInt32 position)
    {
        var payload = JsonSerializer.Serialize(new WindowPlacementState(position.X, position.Y), JsonOptions);
        var temporaryFile = _filePath + ".tmp";
        File.WriteAllText(temporaryFile, payload);
        File.Move(temporaryFile, _filePath, true);
    }

    private sealed record WindowPlacementState(int X, int Y);
}
