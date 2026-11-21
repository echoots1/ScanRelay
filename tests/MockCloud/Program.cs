namespace ScanRelay.MockCloud;

// Runs the mock on its own, for trying the app on a development machine.
// Prints the addresses to give the service and seeds a few made-up people.
internal static class MockCloudProgram
{
    private static async Task Main(string[] args)
    {
        var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5090;
        await using var server = await MockCloudServer.StartAsync(port);
        var admin = server.State.AddUser("Alex Admin");
        var helper = server.State.AddUser("Harper Helpdesk");
        var staff = new[] { "Sam Scanner", "Robin Reader", "Casey Copier", "Jordan Jam", "Taylor Toner" }
            .Select(n => server.State.AddUser(n)).ToArray();
        server.State.AddUser("Nora NoDrive", hasDrive: false);
        server.State.AddGroup("ScanRelay Admins", admin);
        server.State.AddGroup("ScanRelay Helpdesk", helper);
        server.State.AddGroup("All Staff", [.. staff, admin]);
        server.State.AddGroup("Front Office", staff[0], staff[1]);
        server.State.SignInAs = admin.Id;

        Console.WriteLine($"Mock Microsoft 365 is running. Tenant {server.State.TenantId}");
        Console.WriteLine($"  --Cloud:Login={server.LoginUrl}");
        Console.WriteLine($"  --Cloud:Graph={server.GraphUrl}");
        Console.WriteLine("Press Ctrl+C to stop.");
        await Task.Delay(Timeout.Infinite);
    }
}
