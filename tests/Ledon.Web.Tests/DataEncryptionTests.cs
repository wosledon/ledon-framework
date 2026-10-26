using Ledon.Data.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>列加密：落库密文、读取明文、历史明文兼容。</summary>
public class DataEncryptionTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"ledon-encryption-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    [Fact]
    public async Task Encrypted_column_is_ciphertext_at_rest_and_plaintext_via_ef()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataEncryption();
        services.AddDbContext<EncryptedDbContext>(options =>
            options.UseSqlite($"Data Source={_databasePath}"));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EncryptedDbContext>();

        await db.Database.EnsureCreatedAsync();
        db.Customers.Add(new Customer { Name = "Alice", IdCard = "110101199001011234" });
        await db.SaveChangesAsync();

        // 直接读列：必须是密文
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT IdCard FROM Customers LIMIT 1";
        var stored = (string)(await command.ExecuteScalarAsync())!;

        Assert.StartsWith("enc:v1:", stored);
        Assert.DoesNotContain("110101199001011234", stored);

        // 通过 EF 读：透明解密
        db.ChangeTracker.Clear();
        var customer = await db.Customers.SingleAsync();
        Assert.Equal("110101199001011234", customer.IdCard);
    }

    [Fact]
    public void Encryption_service_roundtrips_and_accepts_plaintext_history()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataEncryption();

        using var provider = services.BuildServiceProvider();
        var encryption = provider.GetRequiredService<IEncryptionService>();

        var ciphertext = encryption.Protect("secret");
        Assert.StartsWith("enc:v1:", ciphertext);
        Assert.Equal("secret", encryption.Unprotect(ciphertext));

        // 没有前缀的历史数据按明文返回
        Assert.Equal("legacy", encryption.Unprotect("legacy"));
    }

    private sealed class EncryptedDbContext(
        DbContextOptions<EncryptedDbContext> options,
        IEncryptionService encryption) : DbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyEncryption(encryption);
    }

    private sealed class Customer
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        [Encrypted]
        public string IdCard { get; set; } = "";
    }
}
