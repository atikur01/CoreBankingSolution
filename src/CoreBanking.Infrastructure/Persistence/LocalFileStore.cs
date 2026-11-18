using Microsoft.Extensions.Hosting;

namespace CoreBanking.Infrastructure.Persistence;

public class LocalFileStore(IHostEnvironment environment) : IFileStore
{
    private string UploadDirectory
    {
        get
        {
            var dir = Path.Combine(environment.ContentRootPath, "App_Data", "uploads");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<string> SaveAsync(UploadedFile file, string extension)
    {
        var name = Guid.NewGuid().ToString("N") + extension;
        await using var stream = File.Create(Path.Combine(UploadDirectory, name));
        await file.Content.CopyToAsync(stream);
        return name;
    }

    public string? ResolvePath(string storedName)
    {
        var path = Path.Combine(UploadDirectory, Path.GetFileName(storedName));
        return File.Exists(path) ? path : null;
    }
}
