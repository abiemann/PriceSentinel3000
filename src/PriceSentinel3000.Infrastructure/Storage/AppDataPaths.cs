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

    public static string RobinhoodTokenCache =>
        Path.Combine(ApplicationDirectory, "robinhood-tokens.dat");

    public static string RobinhoodClientRegistration =>
        Path.Combine(ApplicationDirectory, "robinhood-client.dat");

    public static string UserPreferences =>
        Path.Combine(ApplicationDirectory, "preferences.json");
}
