namespace CoreBanking.Domain.Common;

/// <summary>Business-rule violation that is safe to show to the user.</summary>
public class BankingException(string message) : Exception(message);
