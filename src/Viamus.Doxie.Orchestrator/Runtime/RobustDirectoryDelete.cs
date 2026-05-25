namespace Viamus.Doxie.Orchestrator.Runtime;

internal static class RobustDirectoryDelete
{
    public static void Delete(string directory)
    {
        if (!Directory.Exists(directory)) return;

        const int attempts = 6;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                ClearAttributes(directory);
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts - 1 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }

        ClearAttributes(directory);
        Directory.Delete(directory, recursive: true);
    }

    private static void ClearAttributes(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var path in EnumerateFileSystemEntriesSafe(directory))
        {
            try
            {
                var attributes = File.GetAttributes(path);
                File.SetAttributes(path, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        try
        {
            var attributes = File.GetAttributes(directory);
            File.SetAttributes(directory, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static IEnumerable<string> EnumerateFileSystemEntriesSafe(string directory)
    {
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var entry in entries)
            {
                yield return entry;
                try
                {
                    if (Directory.Exists(entry))
                    {
                        stack.Push(entry);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
    }
}
