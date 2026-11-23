using FluentMigrator.Builders.Create.Table;
using Nop.Data.Extensions;
using Nop.Data.Mapping.Builders;
using Nop.Plugin.Payments.Sattleme.Domain;

namespace Nop.Plugin.Payments.Sattleme.Data;

/// <summary>The table of <see cref="SattlemeOrder"/>.</summary>
public partial class SattlemeOrderBuilder : NopEntityBuilder<SattlemeOrder>
{
    /// <summary>Its columns.</summary>
    public override void MapEntity(CreateTableExpressionBuilder table)
    {
        table
            .WithColumn(nameof(SattlemeOrder.OrderGuid)).AsGuid().NotNullable().Unique()
            .WithColumn(nameof(SattlemeOrder.OrderId)).AsInt32().NotNullable()
            .WithColumn(nameof(SattlemeOrder.StoreId)).AsInt32().NotNullable()
            .WithColumn(nameof(SattlemeOrder.CustomerId)).AsInt32().NotNullable().Indexed()
            .WithColumn(nameof(SattlemeOrder.PageToken)).AsString(64).NotNullable()
            .WithColumn(nameof(SattlemeOrder.LockId)).AsString(32).Nullable()
            .WithColumn(nameof(SattlemeOrder.LockedUntilUtc)).AsNopDateTime2().Nullable()
            .WithColumn(nameof(SattlemeOrder.CreatedOnUtc)).AsNopDateTime2().NotNullable();
    }
}