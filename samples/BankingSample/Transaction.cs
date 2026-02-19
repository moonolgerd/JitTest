namespace BankingSample;

public enum TransactionType { Deposit, Withdrawal, Interest }

/// <summary>Immutable record of a single account transaction.</summary>
public record Transaction(TransactionType Type, decimal Amount, decimal BalanceAfter)
{
    public DateTime Timestamp { get; } = DateTime.UtcNow;
}
