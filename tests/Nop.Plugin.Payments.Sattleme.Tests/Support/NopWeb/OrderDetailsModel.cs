using Nop.Web.Framework.Models;

namespace Nop.Web.Models.Order;

/// <summary>
/// Stands for nopCommerce's model of the customer's page of an order (Nop.Web), which the plugin's
/// consumer of its ModelPreparedEvent recognizes by its name: its properties the plugin sets.
/// </summary>
public sealed record OrderDetailsModel : BaseNopEntityModel
{
    /// <summary>Whether the page offers "Cancel order".</summary>
    public bool CanCancelOrder { get; set; }
}