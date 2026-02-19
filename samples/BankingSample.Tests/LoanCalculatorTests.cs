using BankingSample;

namespace BankingSample.Tests;

/// <summary>
/// Unit tests for LoanCalculator.
///
/// These tests cover the main scenarios but deliberately leave boundary conditions
/// untested — a good target for JiTTest.
///
/// Known gaps (for JiTTest to find):
///   - CalculateMonthlyPayment with annualRatePercent = 0 (uses simple division path)
///   - IsEligible exactly at the 28% front-end DTI threshold (boundary — should pass)
///   - IsEligible at 28.01% front-end DTI (should fail)
///   - IsEligible exactly at the 43% back-end DTI threshold (boundary — should pass)
///   - IsEligible at 43.01% back-end DTI (should fail)
///   - CalculateTotalInterest with 0% rate returns 0
/// </summary>
public class LoanCalculatorTests
{
    // ── Monthly payment ─────────────────────────────────────────────────────

    [Fact]
    public void CalculateMonthlyPayment_Standard30YearMortgage_ReturnsCorrectAmount()
    {
        // $300,000 at 6% for 30 years → ~$1,798.65
        var payment = LoanCalculator.CalculateMonthlyPayment(300_000m, 6m, 360);
        Assert.Equal(1798.65m, payment);
    }

    [Fact]
    public void CalculateMonthlyPayment_5Year_CarLoan()
    {
        // $25,000 at 5% for 60 months → ~$471.78
        var payment = LoanCalculator.CalculateMonthlyPayment(25_000m, 5m, 60);
        Assert.Equal(471.78m, payment);
    }

    [Fact]
    public void CalculateMonthlyPayment_ZeroPrincipal_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LoanCalculator.CalculateMonthlyPayment(0m, 5m, 60));
    }

    [Fact]
    public void CalculateMonthlyPayment_NegativeRate_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LoanCalculator.CalculateMonthlyPayment(10_000m, -1m, 12));
    }

    [Fact]
    public void CalculateMonthlyPayment_ZeroTerm_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LoanCalculator.CalculateMonthlyPayment(10_000m, 5m, 0));
    }

    // ── Total interest ──────────────────────────────────────────────────────

    [Fact]
    public void CalculateTotalInterest_Standard30YearMortgage_ReturnsPositiveAmount()
    {
        var interest = LoanCalculator.CalculateTotalInterest(300_000m, 6m, 360);
        // $1,798.65 x 360 − $300,000 = $347,514
        Assert.True(interest > 300_000m, "Total interest on a 30-year loan should be substantial.");
    }

    [Fact]
    public void CalculateTotalInterest_IsAlwaysNonNegative()
    {
        var interest = LoanCalculator.CalculateTotalInterest(10_000m, 4m, 24);
        Assert.True(interest >= 0m);
    }

    // ── Eligibility ─────────────────────────────────────────────────────────

    [Fact]
    public void IsEligible_WellWithinBothRatios_ReturnsTrue()
    {
        // income=$10,000 existing=$500 proposed=$1,000 → front-end=10%, back-end=15%
        Assert.True(LoanCalculator.IsEligible(10_000m, 500m, 1_000m));
    }

    [Fact]
    public void IsEligible_FrontEndRatioExceeded_ReturnsFalse()
    {
        // income=$5,000 proposed=$2,000 → front-end=40% > 28%
        Assert.False(LoanCalculator.IsEligible(5_000m, 0m, 2_000m));
    }

    [Fact]
    public void IsEligible_BackEndRatioExceeded_ReturnsFalse()
    {
        // income=$5,000 existing=$1,800 proposed=$700 → front-end=14% OK, back-end=50% > 43%
        Assert.False(LoanCalculator.IsEligible(5_000m, 1_800m, 700m));
    }

    [Fact]
    public void IsEligible_ZeroIncome_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LoanCalculator.IsEligible(0m, 0m, 500m));
    }

    [Fact]
    public void IsEligible_NegativeExistingDebt_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LoanCalculator.IsEligible(5_000m, -100m, 500m));
    }

    [Fact]
    public void IsEligible_ZeroProposedPayment_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            LoanCalculator.IsEligible(5_000m, 200m, 0m));
    }
}
