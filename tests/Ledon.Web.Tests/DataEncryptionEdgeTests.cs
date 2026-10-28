using Ledon.Data.Encryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>列加密边界：空值透传、自定义前缀、非字符串属性拒绝。</summary>
public class DataEncryptionEdgeTests
{
    [Fact]
    public void Converter_passes_through_null_and_empty_values()
    {
        var converter = new EncryptedValueConverter(new PassthroughEncryption());

        Assert.Null(converter.ConvertToProvider(null));
        Assert.Equal("", converter.ConvertToProvider(""));
        Assert.Equal("", converter.ConvertFromProvider(""));
    }

    [Fact]
    public void Custom_cipher_prefix_is_honored()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataEncryption(options => options.CipherPrefix = "enc:test:");

        using var provider = services.BuildServiceProvider();
        var encryption = provider.GetRequiredService<IEncryptionService>();
        var ciphertext = encryption.Protect("secret");

        Assert.StartsWith("enc:test:", ciphertext, StringComparison.Ordinal);
        Assert.Equal("secret", encryption.Unprotect(ciphertext));
    }

    [Fact]
    public void Encrypted_non_string_property_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedonDataEncryption();
        services.AddDbContext<BadDbContext>(options => options.UseSqlite("Data Source=:memory:"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BadDbContext>();

        Assert.Throws<InvalidOperationException>(() => _ = db.Model);
    }

    private sealed class PassthroughEncryption : IEncryptionService
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public string Unprotect(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;
    }

    private sealed class BadDbContext(DbContextOptions<BadDbContext> options, IEncryptionService encryption)
        : DbContext(options)
    {
        public DbSet<BadEntity> Entities => Set<BadEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyEncryption(encryption);
    }

    private sealed class BadEntity
    {
        public int Id { get; set; }

        [Encrypted]
        public int Number { get; set; }
    }
}
