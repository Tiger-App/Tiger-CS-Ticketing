using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Infrastructure.Modules.Ticketing.Configurations;

/// <summary>MVP-Data-Dictionary.md §2.10 / MVP-ERD.md §2.10 — see Ticket's own remarks for the one confirmed relaxation (nullable Unit/Contact FKs, provisional tickets only).</summary>
public class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("Tickets");

        builder.HasKey(t => t.TicketId);
        builder.Property(t => t.TicketId).ValueGeneratedOnAdd();

        builder.Property(t => t.TicketNumber).HasMaxLength(40).IsRequired();
        builder.HasIndex(t => t.TicketNumber).IsUnique();

        builder.Property(t => t.OriginatingDepartmentId).IsRequired();
        builder.Property(t => t.CurrentDepartmentId).IsRequired();
        // Both nullable since the Unclassified phase: a ticket created from an
        // inquiry nobody has read yet genuinely has no category and no judged
        // priority, and placeholder values would be worse than null — they
        // feed reporting, queue sorting and the dashboard as if a human had
        // chosen them (see Ticket.CategoryId / Ticket.PriorityId).
        builder.Property(t => t.CategoryId);
        builder.Property(t => t.PriorityId);

        builder.Property(t => t.TicketStatus).IsRequired();
        builder.Property(t => t.VerificationStatus).IsRequired();
        builder.Property(t => t.EscalationLevel).IsRequired();
        builder.Property(t => t.SlaState).IsRequired();
        builder.Property(t => t.ResolutionOutcome);

        builder.Property(t => t.RequestSummary).HasMaxLength(2000).IsRequired();
        builder.Property(t => t.ReopenCount).IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();

        // Business-rule change: the real CRM Buyer Lookup match (GET
        // /api/crm/buyers) — a distinct identifier space from
        // UnitReferenceId/ContactReferenceId above (see Ticket's own
        // remarks). No FK here: these are the legacy CRM's own identifiers,
        // not a local cache table's primary key.
        builder.Property(t => t.CrmBuyerCustomerId);
        builder.Property(t => t.CrmBuyerLeadId);
        builder.Property(t => t.CrmBuyerUnitId);
        builder.Property(t => t.CrmBuyerProjectId);
        builder.Property(t => t.CrmBuyerCustomerName).HasMaxLength(200);
        builder.Property(t => t.CrmBuyerProjectName).HasMaxLength(200);
        builder.Property(t => t.CrmBuyerUnitNumber).HasMaxLength(50);
        builder.Property(t => t.ManualProjectName).HasMaxLength(200);
        builder.Property(t => t.ManualUnitNumber).HasMaxLength(50);

        // External-lookup verification identity (PACT/Tasleeh — generic, so
        // a future source needs no schema change). Same no-FK discipline as
        // the CrmBuyer* columns above: these are the external system's own
        // identifiers, never a local cache table's key — no PACT cache table
        // exists, deliberately. Indexed by external customer so "this
        // tenant's previous tickets" is answerable without a scan.
        builder.Property(t => t.CustomerVerificationSource).HasMaxLength(32);
        builder.Property(t => t.ExternalCustomerId).HasMaxLength(64);
        builder.Property(t => t.ExternalUnitId).HasMaxLength(64);
        // Snapshot text, sized like their CRM counterparts: the name matches
        // CrmBuyerCustomerName's 200, the email matches the 256 every other
        // email column in the schema uses.
        builder.Property(t => t.ExternalCustomerName).HasMaxLength(200);
        builder.Property(t => t.ExternalCustomerEmail).HasMaxLength(256);
        builder.HasIndex(t => new { t.CustomerVerificationSource, t.ExternalCustomerId })
            .HasFilter("[ExternalCustomerId] IS NOT NULL");

        // MVP-Data-Dictionary.md §2.10 — optimistic concurrency for the
        // assignment/transfer/status-change/resolve/close/reconciliation
        // operations this increment adds (S-13). Deferred by the prior
        // increment, which had nothing yet to guard.
        builder.Property(t => t.RowVersion).IsRowVersion();

        // Who created the ticket — the read-only "ticket creator" access and the "Created by Me" filter. No foreign key: it is an audit-style
        // fact that must never block or cascade, and an index serves the visibility predicate and the filter.
        builder.Property(t => t.CreatedByEmployeeId);
        builder.HasIndex(t => t.CreatedByEmployeeId, "IX_Tickets_CreatedByEmployeeId");

        // First eligible Human Agent to own the ticket (write-once). Audit-style fact, so no foreign key; indexed for diagnostics and the return-to-Call-Center lookup.
        builder.Property(t => t.OriginalHumanAgentId);
        builder.HasIndex(t => t.OriginalHumanAgentId, "IX_Tickets_OriginalHumanAgentId");

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(t => t.OriginatingDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Department>()
            .WithMany()
            .HasForeignKey(t => t.CurrentDepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(t => t.CurrentOwnerEmployeeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable — see this type's own remarks (confirmed relaxation for a
        // provisional/PendingCrmVerification ticket only).
        builder.HasOne<UnitReference>()
            .WithMany()
            .HasForeignKey(t => t.UnitReferenceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ContactReference>()
            .WithMany()
            .HasForeignKey(t => t.ContactReferenceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Category>()
            .WithMany()
            .HasForeignKey(t => t.CategoryId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Priority>()
            .WithMany()
            .HasForeignKey(t => t.PriorityId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Ticket>()
            .WithMany()
            .HasForeignKey(t => t.DuplicateOfTicketId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Workflow/Automation phase 2 — optional request-type classification.
        // Restrict, not cascade: a request type referenced by tickets is
        // deactivated, never deleted (same rule as Departments/Categories).
        builder.HasOne<TigerCS.Domain.Modules.WorkflowConfiguration.RequestType>()
            .WithMany()
            .HasForeignKey(t => t.RequestTypeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // The pinned workflow VERSION. Restrict: a version referenced by any
        // ticket can never be physically deleted.
        builder.HasOne<TigerCS.Domain.Modules.WorkflowConfiguration.WorkflowTemplate>()
            .WithMany()
            .HasForeignKey(t => t.WorkflowTemplateId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
