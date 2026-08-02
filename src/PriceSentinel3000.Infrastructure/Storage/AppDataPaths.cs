namespace PriceSentinel3000.Infrastructure.Storage;

public static class AppDataPaths
{
    public static string ApplicationDirectory
    {
        get
        {
            string localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "PriceSentinel3000");
        }
    }

    public static string JournalDatabase =>
        Path.Combine(ApplicationDirectory, "journal.db");
}
