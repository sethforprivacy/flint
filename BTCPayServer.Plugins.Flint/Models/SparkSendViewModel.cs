using System;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

public class SparkSendViewModel
{
    [BindNever]
    public string StoreId { get; set; } = "";

    /// <summary>A BOLT11 invoice or a Lightning Address (user@domain.com).</summary>
    public string? Destination { get; set; }

    /// <summary>Required for Lightning Address destinations; ignored when the BOLT11 carries its own amount.</summary>
    public long? AmountSats { get; set; }

    /// <summary>Maximum routing fee in satoshi. Null applies the default cap (3% or 25 sat, whichever is larger).</summary>
    public long? MaxFeeSats { get; set; }

    /// <summary>Set after a successful send.</summary>
    public SparkSendResult? Result { get; set; }
}

public class SparkSendResult
{
    public string PaymentId { get; set; } = "";
    public string? PaymentHash { get; set; }
    public long AmountSats { get; set; }
    public long FeeSats { get; set; }
    public DateTimeOffset PaidAt { get; set; }
}
