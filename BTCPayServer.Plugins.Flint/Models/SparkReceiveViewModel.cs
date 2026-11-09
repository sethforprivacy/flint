using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

public class SparkReceiveViewModel
{
    [BindNever]
    public string StoreId { get; set; } = "";

    /// <summary>Optional memo / description for the invoice.</summary>
    public string? Description { get; set; }

    /// <summary>Amount in satoshi. Null generates an amountless invoice.</summary>
    public long? AmountSats { get; set; }

    /// <summary>Set after a successful invoice generation.</summary>
    public SparkReceiveResult? Result { get; set; }
}
