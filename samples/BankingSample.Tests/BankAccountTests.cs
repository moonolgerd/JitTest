using BankingSample;

namespace BankingSample.Tests;

/// <summary>
/// Unit tests for BankAccount.
///
/// These tests cover the main happy paths but deliberately leave several
/// boundary conditions untested — a good target for JiTTest.
///
/// Known gaps (for JiTTest to find):
///   - Withdraw exactly up to the overdraft limit (balance - amount == -OverdraftLimit) should succeed
///   - Withdraw 1 cent beyond the overdraft limit should fail
///   - Deposit of exactly 0 should throw
///   - ApplyMonthlyInterest with annualRate = 0 should leave balance unchanged
///   - Transfer to the same account should throw
///   - Depositing on a closed account should throw
/// </summary>
public class BankAccountTests
{
    [Fact]
    public void Deposit_PositiveAmount_IncreasesBalance()
    {
        var account = new BankAccount("ACC001", "Alice", 100m);
        account.Deposit(50m);
        Assert.Equal(150m, account.Balance);
    }

    [Fact]
    public void Deposit_AddsTransactionRecord()
    {
        var account = new BankAccount("ACC001", "Alice");
        account.Deposit(200m);
        Assert.Single(account.Transactions);
        Assert.Equal(TransactionType.Deposit, account.Transactions[0].Type);
        Assert.Equal(200m, account.Transactions[0].Amount);
    }

    [Fact]
    public void Deposit_NegativeAmount_Throws()
    {
        var account = new BankAccount("ACC001", "Alice");
        Assert.Throws<ArgumentException>(() => account.Deposit(-10m));
    }

    [Fact]
    public void Withdraw_SufficientFunds_DecreasesBalance()
    {
        var account = new BankAccount("ACC001", "Alice", 500m);
        account.Withdraw(200m);
        Assert.Equal(300m, account.Balance);
    }

    [Fact]
    public void Withdraw_ExceedsBalance_WithNoOverdraft_Throws()
    {
        var account = new BankAccount("ACC001", "Alice", 100m);
        Assert.Throws<InvalidOperationException>(() => account.Withdraw(150m));
    }

    [Fact]
    public void Withdraw_WithinOverdraftLimit_Succeeds()
    {
        // Balance = 50, overdraft = 100 → can withdraw up to 150
        var account = new BankAccount("ACC001", "Alice", 50m, overdraftLimit: 100m);
        account.Withdraw(120m); // balance becomes -70, still within overdraft
        Assert.Equal(-70m, account.Balance);
    }

    [Fact]
    public void Withdraw_NegativeAmount_Throws()
    {
        var account = new BankAccount("ACC001", "Alice", 100m);
        Assert.Throws<ArgumentException>(() => account.Withdraw(-5m));
    }

    [Fact]
    public void Transfer_MovesBalanceBetweenAccounts()
    {
        var source = new BankAccount("ACC001", "Alice", 500m);
        var target = new BankAccount("ACC002", "Bob");
        source.Transfer(target, 200m);
        Assert.Equal(300m, source.Balance);
        Assert.Equal(200m, target.Balance);
    }

    [Fact]
    public void Transfer_InsufficientFunds_Throws()
    {
        var source = new BankAccount("ACC001", "Alice", 100m);
        var target = new BankAccount("ACC002", "Bob");
        Assert.Throws<InvalidOperationException>(() => source.Transfer(target, 500m));
    }

    [Fact]
    public void ApplyMonthlyInterest_PositiveRate_IncreasesBalance()
    {
        var account = new BankAccount("ACC001", "Alice", 1200m);
        account.ApplyMonthlyInterest(0.12m); // 12% annual → 1% monthly → +12
        Assert.Equal(1212m, account.Balance);
    }

    [Fact]
    public void ApplyMonthlyInterest_ZeroBalance_NoChange()
    {
        var account = new BankAccount("ACC001", "Alice", 0m);
        account.ApplyMonthlyInterest(0.06m);
        Assert.Equal(0m, account.Balance);
    }

    [Fact]
    public void ApplyMonthlyInterest_NegativeRate_Throws()
    {
        var account = new BankAccount("ACC001", "Alice", 100m);
        Assert.Throws<ArgumentException>(() => account.ApplyMonthlyInterest(-0.05m));
    }

    [Fact]
    public void Close_ActiveAccount_SetsIsActiveFalse()
    {
        var account = new BankAccount("ACC001", "Alice", 100m);
        account.Close();
        Assert.False(account.IsActive);
    }

    [Fact]
    public void Close_AlreadyClosed_Throws()
    {
        var account = new BankAccount("ACC001", "Alice");
        account.Close();
        Assert.Throws<InvalidOperationException>(() => account.Close());
    }

    [Fact]
    public void Withdraw_ClosedAccount_Throws()
    {
        var account = new BankAccount("ACC001", "Alice", 500m);
        account.Close();
        Assert.Throws<InvalidOperationException>(() => account.Withdraw(10m));
    }
}
