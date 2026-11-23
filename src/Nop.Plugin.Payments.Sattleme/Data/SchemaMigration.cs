using FluentMigrator;
using Nop.Data.Extensions;
using Nop.Data.Migrations;
using Nop.Plugin.Payments.Sattleme.Domain;

namespace Nop.Plugin.Payments.Sattleme.Data;

/// <summary>
/// The plugin's tables, made when it is installed, deleted when it is uninstalled (Down), which the
/// plugin refuses while a payment may still come
/// (SattlemePaymentMethod.PreparePluginToUninstallAsync). As nopCommerce 5.00's own plugins write
/// theirs: a Migration whose Up makes what is not there yet and whose Down deletes what is there,
/// nopCommerce 5.00 having replaced Create.TableFor with CreateTableIfNotExists and
/// DeleteTableIfExists (Nop.Data.Extensions.FluentMigratorExtensions).
/// </summary>
[NopMigration("2026-10-24 00:00:00", "Payments.Sattleme base schema", MigrationProcessType.Installation)]
public class SchemaMigration : Migration
{
    private const string CHECKS_INDEX = "IX_SattlemeInvoice_Final_NextCheckOnUtc";

    /// <summary>The tables and the index of the background checks.</summary>
    public override void Up()
    {
        this.CreateTableIfNotExists<SattlemeOrder>();
        this.CreateTableIfNotExists<SattlemeInvoice>();
        if (!Schema.Table(nameof(SattlemeInvoice)).Index(CHECKS_INDEX).Exists())
        {
            Create.Index(CHECKS_INDEX)
                .OnTable(nameof(SattlemeInvoice))
                .OnColumn(nameof(SattlemeInvoice.Final)).Ascending()
                .OnColumn(nameof(SattlemeInvoice.NextCheckOnUtc)).Ascending();
        }
    }

    /// <summary>The tables, their indexes with them.</summary>
    public override void Down()
    {
        this.DeleteTableIfExists<SattlemeInvoice>();
        this.DeleteTableIfExists<SattlemeOrder>();
    }
}