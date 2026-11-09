using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

public class SparkWalletHistoryViewModel
{
    [BindNever]
    public string StoreId { get; set; } = "";

    /// <summary>Sent and received payments, newest first.</summary>
    public IReadOnlyList<SparkPayment> Payments { get; set; } = [];

    /// <summary>null = show all directions.</summary>
    public SparkPaymentDirection? Direction { get; set; }

    /// <summary>0 = all time, otherwise number of days back from now.</summary>
    public int Period { get; set; }

    /// <summary>How many payments were skipped (offset).</summary>
    public int Skip { get; set; }

    /// <summary>Page size requested.</summary>
    public int Count { get; set; } = 25;

    /// <summary>True when there are more payments beyond this page.</summary>
    public bool HasMore { get; set; }
}
