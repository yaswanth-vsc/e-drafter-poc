using Microsoft.EntityFrameworkCore;

namespace EDrafter.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SpendAttempt> SpendAttempts => Set<SpendAttempt>();
    public DbSet<Agreement> Agreements => Set<Agreement>();
    public DbSet<Signatory> Signatories => Set<Signatory>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SpendAttempt>(e =>
        {
            e.HasKey(x => x.Id);
            // This index is the double-charge guard. Without it the ledger is just a log.
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.OurRefId).HasMaxLength(120);
            e.Property(x => x.EdrafterId).HasMaxLength(120);
            e.Property(x => x.FailureReason).HasMaxLength(1000);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();
        });

        b.Entity<Agreement>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RefId).IsUnique();
            e.HasIndex(x => x.OrderIdd);
            e.HasIndex(x => x.EsignDocumentId);
            e.HasIndex(x => x.ZohoRequestId);
            e.Property(x => x.RefId).HasMaxLength(120).IsRequired();
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.ConsiderationAmount).HasPrecision(18, 2);
            e.Property(x => x.MonthlyRent).HasPrecision(18, 2);
            e.Property(x => x.Denomination).HasPrecision(18, 2);

            e.HasMany(x => x.Signatories)
                .WithOne(s => s.Agreement!)
                .HasForeignKey(s => s.AgreementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Signatory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(200).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20);
        });

        b.Entity<WebhookEvent>(e =>
        {
            // The key IS the dedupe. A duplicate delivery collides and is skipped.
            e.HasKey(x => x.EventKey);
            e.Property(x => x.EventKey).HasMaxLength(200);
            e.Property(x => x.EventName).HasMaxLength(100);
        });
    }
}
