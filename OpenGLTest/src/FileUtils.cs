using System.IO;

namespace silkgltriangle;

public static class FileUtils
{
    public static string ReadFile(string path) => File.ReadAllText(path);
}