using System.IO;

namespace VoxelEngine.Core;

public static class FileUtils
{
    public static string ReadFile(string path) => File.ReadAllText(path);
}