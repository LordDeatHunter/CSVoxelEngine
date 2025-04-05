using System.IO;

namespace silkgltriangle;

public static class FileUtils
{
    public static string ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}