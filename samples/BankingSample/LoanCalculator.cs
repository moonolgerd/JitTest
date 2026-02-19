namespace BankingSample;

/// <summary>
/// Standard mortgage/loan calculations using the PMT formula.
/// Eligibility is assessed using front-end and back-end debt-to-income ratios.
/// </summary>
public static class LoanCalculator
{
    /// <summary>Max ratio of proposed payment to gross monthly income (28 %).</summary>
    public const double MaxFrontEndDti = 0.28;

    /// <summary>Max ratio of total monthly debt (existing + proposed) to gross income (43 %).</summary>
    public const double MaxBackEndDti = 0.43;

    /// <summary>
    /// Calculates the fixed monthly payment for a loan using the standard PMT formula.
    /// Rounds to the nearest cent (half-up).
    /// </summary>
    /// <param name="principal">Loan amount in currency units. Must be > 0.</param>
    /// <param name="annualRatePercent">Annual interest rate as a percentage (e.g. 6.5 for 6.5%). Must be &gt;= 0.</param>
    /// <param name="termMonths">Number of monthly payments. Must be > 0.</param>
    public static decimal CalculateMonthlyPayment(
        decimal principal, decimal annualRatePercent, int termMonths)
    {
        if (principal <= 0)
            throw new ArgumentException("Principal must be greater than zero.", nameof(principal));
        if (annualRatePercent < 0)
            throw new ArgumentException("Annual rate cannot be negative.", nameof(annualRatePercent));
        if (termMonths <= 0)
            throw new ArgumentException("Term must be at least one month.", nameof(termMonths));

        // When rate is 0 just divide evenly
        if (annualRatePercent == 0)
            return Math.Round(principal / termMonths, 2, MidpointRounding.AwayFromZero);

        var r = (double)(annualRatePercent / 100m / 12m);
        var payment = (double)principal * r / (1 - Math.Pow(1 + r, -termMonths));
        return Math.Round((decimal)payment, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Returns the total interest paid over the life of the loan.
    /// </summary>
    public static decimal CalculateTotalInterest(
        decimal principal, decimal annualRatePercent, int termMonths)
    {
        var monthly = CalculateMonthlyPayment(principal, annualRatePercent, termMonths);
        return Math.Round(monthly * termMonths - principal, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Determines whether an applicant meets the standard DTI eligibility thresholds.
    /// </summary>
    /// <param name="grossMonthlyIncome">Applicant's gross monthly income. Must be > 0.</param>
    /// <param name="existingMonthlyDebt">Sum of all existing monthly debt payments (excluding the new loan).</param>
    /// <param name="proposedMonthlyPayment">Monthly payment for the loan being considered.</param>
    public static bool IsEligible(
        decimal grossMonthlyIncome,
        decimal existingMonthlyDebt,
        decimal proposedMonthlyPayment)
    {
        if (grossMonthlyIncome <= 0)
            throw new ArgumentException("Monthly income must be greater than zero.", nameof(grossMonthlyIncome));
        if (existingMonthlyDebt < 0)
            throw new ArgumentException("Existing debt cannot be negative.", nameof(existingMonthlyDebt));
        if (proposedMonthlyPayment <= 0)
            throw new ArgumentException("Proposed payment must be greater than zero.", nameof(proposedMonthlyPayment));

        var frontEnd = (double)(proposedMonthlyPayment / grossMonthlyIncome);
        var backEnd  = (double)((existingMonthlyDebt + proposedMonthlyPayment) / grossMonthlyIncome);

        return frontEnd <= MaxFrontEndDti && backEnd <= MaxBackEndDti;
    }
}
