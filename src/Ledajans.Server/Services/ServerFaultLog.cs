namespace Ledajans.Server.Services;

public static class ServerFaultLog
{
    private static readonly object Gate = new();

    public static void Write(string contentRoot, string fileName, string text)
    {
        try
        {
            var dir = Path.Combine(contentRoot, "logs");
            Directory.CreateDirectory(dir);
            lock (Gate)
            {
                File.AppendAllText(
                    Path.Combine(dir, fileName),
                    $"[{DateTime.UtcNow:O}]{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Kayıt yazılamazsa istek yine de yanıt vermeli.
        }
    }
}
