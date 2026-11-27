using System.IO.Compression;

namespace ScanRelay.Installer.Tests;

public sealed class CopyRetryTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("scanrelay-copy-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static MemoryStream Payload()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { WindowsSteps.ServiceExe, "wwwroot/app.css" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("content of " + name);
            }
        }

        return stream;
    }

    [Fact]
    public void The_program_files_can_be_copied_a_second_time_from_the_same_payload()
    {
        // The installer's file steps are written for Windows paths, so this runs on the Windows build only.
        if (!OperatingSystem.IsWindows()) return;
        using var payload = Payload();
        var log = new SetupLog(Path.Combine(_folder, "setup.log"), null, null, 0);
        var target = Path.Combine(_folder, "program");

        WindowsSteps.ExtractPayload(log, payload, target);
        File.Delete(Path.Combine(target, WindowsSteps.ServiceExe));
        WindowsSteps.ExtractPayload(log, payload, target);

        Assert.Equal("content of " + WindowsSteps.ServiceExe, File.ReadAllText(Path.Combine(target, WindowsSteps.ServiceExe)));
        Assert.True(File.Exists(Path.Combine(target, "wwwroot", "app.css")));
    }
}
