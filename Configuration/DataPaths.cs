namespace ProxyCraft.Configuration;

public sealed class DataPaths
{
    public DataPaths(IConfiguration configuration, IWebHostEnvironment environment)
    {
        string configuredDirectory = configuration["Storage:Directory"] ?? "App_Data";
        DirectoryPath = Path.GetFullPath(configuredDirectory, environment.ContentRootPath);
    }

    public string DirectoryPath
    {
        get;
    }

    public string DatabasePath => Path.Combine(DirectoryPath, "proxycraft.db");

    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
}
