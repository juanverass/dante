using Dante.Infrastructure.Contextos;

namespace Dante.Tests;

public sealed class GeneralWorkspaceTests
{
    [Fact]
    public void CreatesPersistentWorkspaceOutsideProject()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dante-general-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = System.IO.Path.Combine(root, "general");
            var workspace = new GeneralWorkspace(path);
            Assert.True(Directory.Exists(workspace.Path));
            File.WriteAllText(System.IO.Path.Combine(path, "state.txt"), "kept");
            Assert.Equal(workspace.Path, new GeneralWorkspace(path).Path);
            Assert.True(File.Exists(System.IO.Path.Combine(path, "state.txt")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
