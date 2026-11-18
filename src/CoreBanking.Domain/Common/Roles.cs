namespace CoreBanking.Domain.Common;

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Manager = "Manager";
    public const string Supervisor = "Supervisor";
    public const string Officer = "Officer";
    public const string Teller = "Teller";
    public static readonly string[] All = [Administrator, Manager, Supervisor, Officer, Teller];

    // Policy role lists
    public const string Makers = "Administrator,Manager,Supervisor,Officer,Teller";
    public const string Checkers = "Administrator,Manager,Supervisor";
    public const string Setup = "Administrator,Manager";
    public const string Operations = "Administrator,Manager,Supervisor,Officer";
}
