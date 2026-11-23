using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Sattleme.Models;

/// <summary>What the checkout says of the method: its description, as paragraphs of plain text.</summary>
public partial record PaymentInfoModel : BaseNopModel
{
    /// <summary>The paragraphs (the view encodes them).</summary>
    public IList<string> Paragraphs { get; set; } = [];
}