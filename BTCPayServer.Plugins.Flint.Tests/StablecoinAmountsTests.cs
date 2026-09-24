using System.Numerics;
using BTCPayServer.Plugins.Flint.Services;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The unit arithmetic between a six-decimal prompt and a route at 6, 8 or 18 decimals.
/// </summary>
/// <remarks>
/// Every rounding here goes the merchant's way — a due up into base units, a payment down out of them — and every
/// case is one a wrong assumption about decimals would misprice by orders of magnitude.
/// </remarks>
public class StablecoinAmountsTests
{
    [Theory]
    [InlineData("10", 6, "10000000")]
    [InlineData("10.5", 6, "10500000")]
    [InlineData("0.000001", 6, "1")]
    [InlineData("10.087341", 18, "10087341000000000000")]
    [InlineData("12.34", 8, "1234000000")]
    public void A_due_converts_exactly_into_base_units(string amount, int decimals, string expected)
    {
        Assert.Equal(BigInteger.Parse(expected), StablecoinAmounts.ToBaseUnits(decimal.Parse(amount), decimals));
    }

    [Fact]
    public void A_due_finer_than_the_route_rounds_up()
    {
        // 12.3456789 on a six-decimal route: the seventh digit is not payable, so the quote asks for the next unit.
        Assert.Equal(new BigInteger(12_345_679), StablecoinAmounts.ToBaseUnits(12.3456789m, 6));
    }

    [Theory]
    [InlineData("10087341234567890123", 18, "10.087341")]
    [InlineData("10087341", 6, "10.087341")]
    [InlineData("1234567899", 8, "12.345678")]
    [InlineData("1", 18, "0")]
    public void A_payment_converts_out_of_base_units_rounding_down(string baseUnits, int decimals, string expected)
    {
        Assert.Equal(
            decimal.Parse(expected),
            StablecoinAmounts.FromBaseUnits(BigInteger.Parse(baseUnits), decimals, StablecoinPayments.Divisibility));
    }

    [Fact]
    public void A_deposit_on_a_finer_route_rounds_up_to_the_prompt()
    {
        var eighteen = BigInteger.Parse("10087341234567890123");

        Assert.Equal(
            BigInteger.Parse("10087342000000000000"),
            StablecoinAmounts.RoundUpToDivisibility(eighteen, 18, StablecoinPayments.Divisibility));
        Assert.Equal(
            BigInteger.Parse("10087342000000000000"),
            StablecoinAmounts.RoundUpToDivisibility(BigInteger.Parse("10087342000000000000"), 18, 6));
        Assert.Equal(new BigInteger(10_087_341), StablecoinAmounts.RoundUpToDivisibility(10_087_341, 6, 6));
    }

    [Fact]
    public void A_unique_ask_takes_the_rounded_amount_when_nobody_else_asks_for_it()
    {
        Assert.Equal(
            new BigInteger(10_087_341),
            StablecoinAmounts.UniqueAsk(10_087_341, 6, 6, new HashSet<BigInteger>()));
    }

    [Fact]
    public void A_unique_ask_steps_by_the_prompts_smallest_unit_past_every_live_ask()
    {
        var taken = new HashSet<BigInteger> { 10_087_341, 10_087_342 };
        Assert.Equal(new BigInteger(10_087_343), StablecoinAmounts.UniqueAsk(10_087_341, 6, 6, taken));

        // On an 18-decimal route the step is a millionth of a token, not a base unit: a payer's wallet shows six
        // decimals, and an ask that differed only in the twelfth could not be told apart by what they send.
        var step = BigInteger.Pow(10, 12);
        var rounded = BigInteger.Parse("10087342000000000000");
        var bscTaken = new HashSet<BigInteger> { rounded };
        Assert.Equal(rounded + step, StablecoinAmounts.UniqueAsk(rounded, 18, 6, bscTaken));
    }

    [Fact]
    public void A_unique_ask_gives_up_rather_than_nudging_without_bound()
    {
        var taken = Enumerable.Range(0, StablecoinAmounts.MaxUniquenessSteps + 1)
            .Select(i => new BigInteger(10_000_000 + i))
            .ToHashSet();

        Assert.Null(StablecoinAmounts.UniqueAsk(10_000_000, 6, 6, taken));
    }

    [Theory]
    [InlineData("10087341", 6, "10.087341")]
    [InlineData("10000000", 6, "10")]
    [InlineData("10087342000000000000", 18, "10.087342")]
    [InlineData("5", 6, "0.000005")]
    public void An_amount_reads_as_the_exact_decimal(string baseUnits, int decimals, string expected)
    {
        Assert.Equal(expected, StablecoinAmounts.Format(BigInteger.Parse(baseUnits), decimals));
    }
}
