namespace CoreBanking.Application.Common.Abstractions;

public record UploadedFile(Stream Content, string FileName, long Length);

public interface IFileStore
{
    Task<string> SaveAsync(UploadedFile file, string extension);
    string? ResolvePath(string storedName);
}
