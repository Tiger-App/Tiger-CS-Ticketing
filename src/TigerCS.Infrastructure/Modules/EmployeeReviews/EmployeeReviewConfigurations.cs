using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.EmployeeReviews;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Infrastructure.Modules.EmployeeReviews;

public class EmployeeReviewDeviceConfiguration : IEntityTypeConfiguration<EmployeeReviewDevice>
{
    public void Configure(EntityTypeBuilder<EmployeeReviewDevice> builder)
    {
        builder.ToTable("EmployeeReviewDevices");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.DeviceName).IsRequired().HasMaxLength(EmployeeReviewDevice.NameMaxLength);
        builder.Property(d => d.EmployeeId).IsRequired();

        // SHA-256 of the random device token; the token itself is never stored. NULL once the credential is revoked.
        builder.Property(d => d.TokenHash).HasColumnType("varbinary(32)");
        builder.Property(d => d.IsActive).IsRequired();
        builder.Property(d => d.CreatedAtUtc).IsRequired();
        builder.Property(d => d.UpdatedAtUtc).IsRequired();
        builder.Property(d => d.LastSeenAtUtc);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(d => d.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // The lookup key for every tablet call. Unique among issued credentials; revoked tablets (NULL) are not indexed.
        builder.HasIndex(d => d.TokenHash)
            .IsUnique()
            .HasFilter("[TokenHash] IS NOT NULL")
            .HasDatabaseName("UX_EmployeeReviewDevices_TokenHash");
        builder.HasIndex(d => d.EmployeeId).HasDatabaseName("IX_EmployeeReviewDevices_EmployeeId");
    }
}

public class EmployeeReviewConfiguration : IEntityTypeConfiguration<EmployeeReview>
{
    public void Configure(EntityTypeBuilder<EmployeeReview> builder)
    {
        builder.ToTable("EmployeeReviews", t => t.HasCheckConstraint("CK_EmployeeReviews_Rating", "[Rating] BETWEEN 1 AND 5"));

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.DeviceId).IsRequired();
        // The employee at the moment of the review: copied from the tablet, never joined through it, so reassigning the tablet keeps history.
        builder.Property(r => r.EmployeeId).IsRequired();
        builder.Property(r => r.Rating).IsRequired();
        builder.Property(r => r.Comment).HasMaxLength(EmployeeReview.CommentMaxLength);
        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.SubmissionId).IsRequired();

        builder.HasOne<EmployeeReviewDevice>()
            .WithMany()
            .HasForeignKey(r => r.DeviceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // The idempotency key: the database, not the application, stops a retried or doubled submission from being stored twice.
        builder.HasIndex(r => r.SubmissionId).IsUnique().HasDatabaseName("UX_EmployeeReviews_SubmissionId");
        builder.HasIndex(r => new { r.EmployeeId, r.CreatedAtUtc }).HasDatabaseName("IX_EmployeeReviews_EmployeeId_CreatedAtUtc");
        builder.HasIndex(r => r.CreatedAtUtc).HasDatabaseName("IX_EmployeeReviews_CreatedAtUtc");
        builder.HasIndex(r => r.DeviceId).HasDatabaseName("IX_EmployeeReviews_DeviceId");
    }
}
