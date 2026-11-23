using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;
using Moq;
using Nop.Data;
using Nop.Plugin.Payments.Sattleme.Domain;

namespace Nop.Plugin.Payments.Sattleme.Tests.Support;

/// <summary>
/// The plugin's two tables in a database of their own: a SQLite file (nopCommerce's own tests run
/// theirs on SQLite through linq2db, as here), with the columns, keys and unique indexes of the
/// plugin's migration. Each request of a test has its own connection, as each request of a shop:
/// the plugin's conditional updates meet as they meet on a shop's database.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private static readonly MappingSchema _mapping = Mapping();
    private readonly List<DataConnection> _connections = [];
    private readonly string _directory;
    private readonly string _connectionString;

    /// <summary>A new, empty database.</summary>
    public TestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "sattleme-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "shop.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();
        using var connection = Connect();
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute("""
            CREATE TABLE "SattlemeOrder" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "OrderGuid" BLOB NOT NULL UNIQUE,
                "OrderId" INTEGER NOT NULL,
                "StoreId" INTEGER NOT NULL,
                "CustomerId" INTEGER NOT NULL,
                "PageToken" TEXT NOT NULL,
                "LockId" TEXT NULL,
                "LockedUntilUtc" TEXT NULL,
                "CreatedOnUtc" TEXT NOT NULL)
            """);
        connection.Execute("""
            CREATE TABLE "SattlemeInvoice" (
                "Id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "OrderGuid" BLOB NOT NULL,
                "Reference" TEXT NOT NULL UNIQUE,
                "PriceAmount" TEXT NOT NULL,
                "PriceCurrency" TEXT NOT NULL,
                "InvoiceJson" TEXT NULL,
                "ExpiresAt" INTEGER NOT NULL,
                "StatusJson" TEXT NULL,
                "State" TEXT NULL,
                "Received" INTEGER NOT NULL,
                "Final" INTEGER NOT NULL,
                "CompletionClaimedOnUtc" TEXT NULL,
                "CreatedOnUtc" TEXT NOT NULL,
                "CheckedOnUtc" TEXT NULL,
                "NextCheckOnUtc" TEXT NULL,
                "LeaseId" TEXT NULL,
                "LeaseUntilUtc" TEXT NULL,
                "Error" TEXT NOT NULL,
                "Errors" INTEGER NOT NULL,
                "Shredded" INTEGER NOT NULL)
            """);
        connection.Execute("""CREATE INDEX "IX_SattlemeInvoice_OrderGuid" ON "SattlemeInvoice" ("OrderGuid")""");
        connection.Execute("""CREATE INDEX "IX_SattlemeInvoice_Final_NextCheckOnUtc" ON "SattlemeInvoice" ("Final", "NextCheckOnUtc")""");
    }

    /// <summary>
    /// nopCommerce's data provider, as one request has it: the plugin's tables through a connection
    /// of its own (the plugin uses GetTable and InsertEntityAsync only).
    /// </summary>
    public INopDataProvider NewProvider()
    {
        var connection = Connect();
        lock (_connections)
            _connections.Add(connection);
        var provider = new Mock<INopDataProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GetTable<SattlemeOrder>()).Returns(() => connection.GetTable<SattlemeOrder>());
        provider.Setup(p => p.GetTable<SattlemeInvoice>()).Returns(() => connection.GetTable<SattlemeInvoice>());
        provider.Setup(p => p.InsertEntityAsync(It.IsAny<SattlemeOrder>())).Returns((SattlemeOrder entity) =>
        {
            entity.Id = connection.InsertWithInt32Identity(entity);
            return Task.FromResult(entity);
        });
        provider.Setup(p => p.InsertEntityAsync(It.IsAny<SattlemeInvoice>())).Returns((SattlemeInvoice entity) =>
        {
            entity.Id = connection.InsertWithInt32Identity(entity);
            return Task.FromResult(entity);
        });
        return provider.Object;
    }

    /// <summary>The invoices, as stored, oldest first.</summary>
    public IList<SattlemeInvoice> Invoices()
    {
        using var connection = Connect();
        return [.. connection.GetTable<SattlemeInvoice>().OrderBy(invoice => invoice.Id)];
    }

    /// <summary>The orders' records.</summary>
    public IList<SattlemeOrder> Orders()
    {
        using var connection = Connect();
        return [.. connection.GetTable<SattlemeOrder>().OrderBy(order => order.Id)];
    }

    /// <summary>Changes the stored invoice <paramref name="id"/> as a test needs it (what another request, or time, did).</summary>
    public void Change(int id, Action<SattlemeInvoice> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var connection = Connect();
        var invoice = connection.GetTable<SattlemeInvoice>().Single(invoice => invoice.Id == id);
        change(invoice);
        connection.Update(invoice);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_connections)
        {
            foreach (var connection in _connections)
                connection.Dispose();
            _connections.Clear();
        }
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Left in the temporary directory: removed with it.
        }
    }

    private DataConnection Connect()
    {
        return new DataConnection(new DataOptions().UseSQLite(_connectionString, SQLiteProvider.Microsoft).UseMappingSchema(_mapping));
    }

    /// <summary>The tables as nopCommerce maps its entities: the class's name, Id its identity key.</summary>
    private static MappingSchema Mapping()
    {
        var schema = new MappingSchema();
        var builder = new FluentMappingBuilder(schema);
        builder.Entity<SattlemeOrder>().HasTableName(nameof(SattlemeOrder)).Property(order => order.Id).IsPrimaryKey().IsIdentity();
        builder.Entity<SattlemeInvoice>().HasTableName(nameof(SattlemeInvoice)).Property(invoice => invoice.Id).IsPrimaryKey().IsIdentity();
        builder.Build();
        return schema;
    }
}