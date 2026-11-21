namespace ScanRelay.Core.Tests;

public class DataFolderTests
{
    [Fact]
    public void Everything_lives_under_the_one_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "scanrelay-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = new DataFolder(root);
            folder.EnsureCreated();

            Assert.True(Directory.Exists(folder.Inbox));
            Assert.True(Directory.Exists(folder.Store));
            Assert.True(Directory.Exists(folder.Logs));
            foreach (var path in new[] { folder.Inbox, folder.Store, folder.Logs, folder.Keys, folder.Database })
            {
                Assert.Equal(folder.Root, Path.GetDirectoryName(path));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void An_empty_root_is_refused(string root)
    {
        Assert.Throws<ArgumentException>(() => new DataFolder(root));
    }
}
