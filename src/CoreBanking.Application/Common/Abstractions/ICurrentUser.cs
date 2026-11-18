namespace CoreBanking.Application.Common.Abstractions;

public interface ICurrentUser
{
    string UserName { get; }
    string? UserId { get; }
    bool IsManagerLevel { get; }
    bool IsChecker { get; }
    bool IsInRole(string role);
    Task<UserProfile?> ProfileAsync();
    Task<int> BranchIdAsync();
}
