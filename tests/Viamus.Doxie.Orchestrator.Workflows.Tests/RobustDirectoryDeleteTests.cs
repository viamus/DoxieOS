using Viamus.Doxie.Orchestrator.Runtime;

namespace Viamus.Doxie.Orchestrator.Workflows.Tests;

public sealed class RobustDirectoryDeleteTests
{
    [Fact]
    public void Delete_removes_readonly_file_tree()
    {
        var root = Path.Combine(Path.GetTempPath(), $"doxie-delete-tests-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        var file = Path.Combine(nested, "readonly.txt");
        Directory.CreateDirectory(nested);
        File.WriteAllText(file, "content");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        File.SetAttributes(nested, File.GetAttributes(nested) | FileAttributes.ReadOnly);

        RobustDirectoryDelete.Delete(root);

        Assert.False(Directory.Exists(root));
    }
}
