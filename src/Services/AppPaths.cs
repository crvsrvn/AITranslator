namespace AITranslator.Services;

public sealed class AppPaths
{
    public static string AppRootDirectory { get; } = FindAppRootDirectory(AppContext.BaseDirectory);

    public AppPaths()
    {
        var applicationDirectory = new DirectoryInfo(AppContext.BaseDirectory).FullName;
        var usesOrganizedLayout = !string.Equals(AppRootDirectory, applicationDirectory, StringComparison.OrdinalIgnoreCase);
        RootDirectory = Path.Combine(AppRootDirectory, "UserData");
        LogsDirectory = Path.Combine(AppRootDirectory, "Logs");
        CacheDirectory = usesOrganizedLayout ? Path.Combine(RootDirectory, "cache") : RootDirectory;
        DictionaryDirectory = usesOrganizedLayout ? Path.Combine(AppRootDirectory, "Dictionary") : RootDirectory;
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DictionaryDirectory);
    }

    public string RootDirectory { get; }

    public string LogsDirectory { get; }

    public string CacheDirectory { get; }

    public string DictionaryDirectory { get; }

    public string SettingsFile => Path.Combine(RootDirectory, "settings.json");

    public string ApiKeyFile => Path.Combine(RootDirectory, "api-key.bin");

    public string CacheDatabase => Path.Combine(CacheDirectory, "cache.db");

    public string DictionaryDatabase => Path.Combine(DictionaryDirectory, "ecdict.db");

    public string WindowPlacementFile => Path.Combine(RootDirectory, "window-placement.json");

    private static string FindAppRootDirectory(string baseDirectory)
    {
        var applicationDirectory = new DirectoryInfo(baseDirectory);
        var dependenciesDirectory = applicationDirectory.Parent;
        if (dependenciesDirectory is not null &&
            string.Equals(dependenciesDirectory.Name, "Dependencies", StringComparison.OrdinalIgnoreCase) &&
            dependenciesDirectory.Parent is not null)
        {
            return dependenciesDirectory.Parent.FullName;
        }

        return applicationDirectory.FullName;
    }
}
