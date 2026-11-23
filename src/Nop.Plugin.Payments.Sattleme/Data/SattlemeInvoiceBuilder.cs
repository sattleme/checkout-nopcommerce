using FluentMigrator.Builders.Create.Table;
using Nop.Data.Extensions;
using Nop.Data.Mapping.Builders;
using Nop.Plugin.Payments.Sattleme.Domain;

namespace Nop.Plugin.Payments.Sattleme.Data;

/// <summary>The table of <see cref="SattlemeInvoice"/>.</summary>
public partial class SattlemeInvoiceBuilder : NopEntityBuilder<SattlemeInvoice>
{
    /// <summary>Its columns.</summary>
    public override void MapEntity(CreateTableExpressionBuilder table)
    {
        table
            .WithColumn(nameof(SattlemeInvoice.OrderGuid)).AsGuid().NotNullable().Indexed()
            .WithColumn(nameof(SattlemeInvoice.Reference)).AsString(64).NotNullable().Unique()
            .WithColumn(nameof(SattlemeInvoice.PriceAmount)).AsString(32).NotNullable()
            .WithColumn(nameof(SattlemeInvoice.PriceCurrency)).AsString(3).NotNullable()
            .WithColumn(nameof(SattlemeInvoice.InvoiceJson)).AsString(int.MaxValue).Nullable()
            .WithColumn(nameof(SattlemeInvoice.ExpiresAt)).AsInt64().NotNullable()
            .WithColumn(nameof(SattlemeInvoice.StatusJson)).AsString(int.MaxValue).Nullable()
            .WithColumn(nameof(SattlemeInvoice.State)).AsString(16).Nullable()
            .WithColumn(nameof(SattlemeInvoice.Received)).AsInt64().NotNullable()
            .WithColumn(nameof(SattlemeInvoice.Final)).AsBoolean().NotNullable()
            .WithColumn(nameof(SattlemeInvoice.CompletionClaimedOnUtc)).AsNopDateTime2().Nullable()
            .WithColumn(nameof(SattlemeInvoice.CreatedOnUtc)).AsNopDateTime2().NotNullable()
            .WithColumn(nameof(SattlemeInvoice.CheckedOnUtc)).AsNopDateTime2().Nullable()
            .WithColumn(nameof(SattlemeInvoice.NextCheckOnUtc)).AsNopDateTime2().Nullable()
            .WithColumn(nameof(SattlemeInvoice.LeaseId)).AsString(32).Nullable()
            .WithColumn(nameof(SattlemeInvoice.LeaseUntilUtc)).AsNopDateTime2().Nullable()
            .WithColumn(nameof(SattlemeInvoice.Error)).AsString(64).NotNullable()
            .WithColumn(nameof(SattlemeInvoice.Errors)).AsInt32().NotNullable()
            .WithColumn(nameof(SattlemeInvoice.Shredded)).AsBoolean().NotNullable();
    }
}