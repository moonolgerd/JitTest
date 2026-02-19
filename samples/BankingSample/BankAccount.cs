namespace BankingSample;

/// <summary>
/// A simple bank account supporting deposits, withdrawals, transfers,
/// optional overdraft protection, and monthly interest application.
/// </summary>
public class BankAccount
{
    private readonly List<Transaction> _transactions = new();
    private decimal _balance;

    public string AccountNumber { get; }
    public string OwnerName { get; }
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Maximum amount the account is allowed to go negative.
    /// A value of 0 means no overdraft is permitted.
    /// </summary>
    public decimal OverdraftLimit { get; }

    public decimal Balance => _balance;
    public IReadOnlyList<Transaction> Transactions => _transactions.AsReadOnly();

    public BankAccount(string accountNumber, string ownerName,
        decimal initialBalance = 0m, decimal overdraftLimit = 0m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        if (initialBalance < 0)
            throw new ArgumentException("Initial balance cannot be negative.", nameof(initialBalance));
        if (overdraftLimit < 0)
            throw new ArgumentException("Overdraft limit cannot be negative.", nameof(overdraftLimit));

        AccountNumber = accountNumber;
        OwnerName = ownerName;
        _balance = initialBalance;
        OverdraftLimit = overdraftLimit;
    }

    /// <summary>Deposits a positive amount into the account.</summary>
    public void Deposit(decimal amount)
    {
        EnsureActive();
        if (amount <= 0)
            throw new ArgumentException("Deposit amount must be greater than zero.", nameof(amount));

        _balance += amount;
        _transactions.Add(new Transaction(TransactionType.Deposit, amount, _balance));
    }

    /// <summary>
    /// Withdraws a positive amount from the account.
    /// Allowed as long as: balance - amount >= -OverdraftLimit.
    /// </summary>
    public void Withdraw(decimal amount)
    {
        EnsureActive();
        if (amount <= 0)
            throw new ArgumentException("Withdrawal amount must be greater than zero.", nameof(amount));
        if (_balance - amount < -OverdraftLimit)
            throw new InvalidOperationException(
                $"Insufficient funds. Available including overdraft: {_balance + OverdraftLimit:C}");

        _balance -= amount;
        _transactions.Add(new Transaction(TransactionType.Withdrawal, amount, _balance));
    }

    /// <summary>Transfers an amount from this account to <paramref name="target"/>.</summary>
    public void Transfer(BankAccount target, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target == this)
            throw new ArgumentException("Cannot transfer to the same account.", nameof(target));

        Withdraw(amount);
        target.Deposit(amount);
    }

    /// <summary>
    /// Applies one month of interest at the given annual rate.
    /// No interest is applied when the balance is zero or negative.
    /// </summary>
    public void ApplyMonthlyInterest(decimal annualRate)
    {
        EnsureActive();
        if (annualRate < 0)
            throw new ArgumentException("Annual rate cannot be negative.", nameof(annualRate));
        if (_balance <= 0) return;

        var monthlyRate = annualRate / 12m;
        var interest = Math.Round(_balance * monthlyRate, 2, MidpointRounding.AwayFromZero);
        _balance += interest;
        _transactions.Add(new Transaction(TransactionType.Interest, interest, _balance));
    }

    /// <summary>Permanently closes the account. No transactions are allowed afterwards.</summary>
    public void Close()
    {
        if (!IsActive)
            throw new InvalidOperationException("Account is already closed.");
        IsActive = false;
    }

    private void EnsureActive()
    {
        if (!IsActive)
            throw new InvalidOperationException("Cannot perform transactions on a closed account.");
    }
}
