namespace ScanRelay.MockDevices;

/// <summary>Runs one mock printer on its own, for trying the app on a development machine.</summary>
internal static class MockDevicesProgram
{
    private static async Task Main(string[] args)
    {
        var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5091;
        await using var printer = await MockPrinter.StartAsync(port);
        Console.WriteLine($"Mock printer is running at {printer.Address}, port {printer.Port}.");
        Console.WriteLine($"Admin sign-in: {printer.AdminUser} / {printer.AdminPassword}");
        Console.WriteLine("Press Ctrl+C to stop.");
        await Task.Delay(Timeout.Infinite);
    }
}
