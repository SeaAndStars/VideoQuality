using VideoQuality.Core;

namespace VideoQuality.Tests;

public class AppPathsTests
{
    [Fact]
    public void Tools_folder_resolves_to_the_release_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "VideoQuality-1.0");

        Assert.Equal(root, AppPaths.ResolveRoot(Path.Combine(root, "tools") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void App_folder_is_its_own_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "VideoQuality-1.0");

        Assert.Equal(root, AppPaths.ResolveRoot(root + Path.DirectorySeparatorChar));
    }
}
