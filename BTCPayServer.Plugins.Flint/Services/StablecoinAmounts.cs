using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The unit arithmetic between a BTCPay prompt (a six-decimal <see cref="decimal"/>) and a route (an integer in
/// the chain's own base units, at 6, 8 or 18 decimals).
/// </summary>
/// <remarks>
/// Integer arithmetic throughout on the route side, because an 18-decimal amount overflows a <see cref="long"/> at
/// nine dollars and a <see cref="double"/> cannot hold it exactly at all. Every conversion that cannot be exact
/// rounds in the direction that leaves the merchant whole: a due is rounded up into base units, a payment is
/// rounded down out of them.
/// </remarks>
public static class StablecoinAmounts
{
    /// <summary>
    /// The most a uniqueness nudge may add to an asked amount, in steps of the prompt's smallest unit.
    /// </summary>
    /// <remarks>
    /// 999 millionths: under a tenth of a cent, on the one quote in a thousand that needs it. Past this many live
    /// quotes for one exact amount on one route, the next is refused rather than nudged further.
    /// </remarks>
    public const int MaxUniquenessSteps = 999;

    /// <summary><c>10^exponent</c>, exactly.</summary>
    public static BigInteger Pow10(int exponent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exponent);
        return BigInteger.Pow(10, exponent);
    }

    /// <summary>A prompt amount in a route's base units, rounded up to the next base unit.</summary>
    public static BigInteger ToBaseUnits(decimal amount, int decimals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);

        // decimal carries at most 28 fractional digits, so split into whole and fraction and scale the fraction as
        // a string of digits rather than multiplying by 10^18 in decimal, which overflows.
        var whole = decimal.Truncate(amount);
        var fraction = amount - whole;
        var result = new BigInteger(whole) * Pow10(decimals);
        if (fraction == 0m)
            return result;

        var digits = fraction.ToString("0.############################", CultureInfo.InvariantCulture)[2..];
        var kept = digits.Length <= decimals ? digits.PadRight(decimals, '0') : digits[..decimals];
        result += BigInteger.Parse(kept, NumberStyles.None, CultureInfo.InvariantCulture);
        if (digits.Length > decimals && digits[decimals..].TrimEnd('0').Length > 0)
            result += BigInteger.One;
        return result;
    }

    /// <summary>
    /// A route amount as a prompt amount, rounded <em>down</em> to <paramref name="divisibility"/> decimals — so a
    /// credit never records more than arrived.
    /// </summary>
    public static decimal FromBaseUnits(BigInteger baseUnits, int decimals, int divisibility)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decimals);
        ArgumentOutOfRangeException.ThrowIfNegative(divisibility);
        if (baseUnits <= BigInteger.Zero)
            return 0m;

        var kept = decimals > divisibility ? baseUnits / Pow10(decimals - divisibility) : baseUnits;
        var scale = Math.Min(decimals, divisibility);
        var whole = BigInteger.DivRem(kept, Pow10(scale), out var remainder);
        if (whole > new BigInteger(decimal.MaxValue))
            return decimal.MaxValue;
        return (decimal)whole + (decimal)remainder / (decimal)Pow10(scale);
    }

    /// <summary>
    /// Rounds a route amount up to the prompt's precision: a multiple of <c>10^(decimals − divisibility)</c> base
    /// units. Identity on a route that is not finer than the prompt.
    /// </summary>
    public static BigInteger RoundUpToDivisibility(BigInteger baseUnits, int decimals, int divisibility)
    {
        if (decimals <= divisibility)
            return baseUnits;

        var step = Pow10(decimals - divisibility);
        var remainder = BigInteger.Remainder(baseUnits, step);
        return remainder.IsZero ? baseUnits : baseUnits - remainder + step;
    }

    /// <summary>
    /// The smallest amount at or above <paramref name="rounded"/>, in the prompt's smallest steps, that no other
    /// live quote on the route is asking for — or null when the first <see cref="MaxUniquenessSteps"/> are all taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes an exactly-paid receive attributable.</b> The payment that arrives names the amount the
    /// payer deposited but not the address they deposited to, so two live quotes asking for the same amount on the
    /// same route could not be told apart by the one who pays exactly — and paying exactly is what wallets do
    /// with the EIP-681 URI or a copied amount. Nudging the second quote up by a millionth keeps every live ask on
    /// a route distinct, for the price of that millionth.
    /// </para>
    /// <para>
    /// Only needed when two quotes would otherwise ask the same: the SDK sizes each deposit from a live estimate,
    /// so equal asks happen mostly when equal invoices are quoted inside one fiat-cache window. The common case is
    /// no nudge at all.
    /// </para>
    /// </remarks>
    public static BigInteger? UniqueAsk(BigInteger rounded, int decimals, int divisibility, IReadOnlySet<BigInteger> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var step = decimals > divisibility ? Pow10(decimals - divisibility) : BigInteger.One;
        for (var i = 0; i <= MaxUniquenessSteps; i++)
        {
            var candidate = rounded + step * i;
            if (!taken.Contains(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>A route amount as the payer should read it: the exact decimal, no trailing zeros.</summary>
    public static string Format(BigInteger baseUnits, int decimals)
    {
        if (decimals <= 0)
            return baseUnits.ToString(CultureInfo.InvariantCulture);

        var negative = baseUnits.Sign < 0;
        var magnitude = BigInteger.Abs(baseUnits);
        var whole = BigInteger.DivRem(magnitude, Pow10(decimals), out var remainder);
        var fraction = remainder.ToString(CultureInfo.InvariantCulture).PadLeft(decimals, '0').TrimEnd('0');
        var text = fraction.Length == 0
            ? whole.ToString(CultureInfo.InvariantCulture)
            : $"{whole.ToString(CultureInfo.InvariantCulture)}.{fraction}";
        return negative ? "-" + text : text;
    }
}
