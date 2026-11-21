namespace ScanRelay.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var f = new Form { Text = "x" };
        return 0;
    }
}
