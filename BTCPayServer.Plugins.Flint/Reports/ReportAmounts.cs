using System;
using System.Numerics;
using BTCPayServer.Services.Reporting;

namespace BTCPayServer.Plugins.Flint.Reports;

/// <summary>Amounts in the shape BTCPay's report page reads: a value and the decimals to show it with.</summary>
internal static class ReportAmounts
{
    public static object? FromDecimal(decimal value, int decimals) =>
        decimals is < 0 or > 28 ? null : new FormattedAmount(value, decimals).ToJObject();

    /// <summary>A base-units integer — satoshi, or a token's smallest unit — at <paramref name="decimals"/>.</summary>
    /// <remarks>Null for a figure <see cref="decimal"/> cannot hold, rather than an exception out of a report.</remarks>
    public static object? FromBaseUnits(BigInteger baseUnits, int decimals)
    {
        if (decimals is < 0 or > 28)
            return null;
        try
        {
            var value = (decimal)baseUnits;
            for (var i = 0; i < decimals; i++)
                value /= 10m;
            return FromDecimal(value, decimals);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
