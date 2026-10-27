using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Kanban.Core;

namespace Kanban.Infrastructure.Persistence;

public sealed class KanbanDbContext(DbContextOptions<KanbanDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable("DataProtectionKeys", "infrastructure");
            entity.Property(key => key.FriendlyName).HasMaxLength(256);
        });
        var owned = new[]
        {
            typeof(Company), typeof(Membership), typeof(Team), typeof(Board), typeof(WorkItem),
            typeof(CompanyGroup), typeof(GroupMember), typeof(MemberGrant), typeof(GroupGrant),
            typeof(Invitation), typeof(ClientApproval), typeof(ConnectionGrant), typeof(ConnectionTeam),
            typeof(AuditEvent), typeof(MutationReceipt)
        };
        foreach (var type in owned)
        {
            var entity = modelBuilder.Entity(type);
            entity.HasBaseType((Type?)null);
            entity.ToTable(type.Name, "app");
            entity.HasKey(nameof(CompanyRow.CompanyId), nameof(CompanyRow.Id));
            entity.Property<byte[]>(nameof(CompanyRow.Version)).IsRowVersion();
            if (type != typeof(Company))
                entity.HasOne(typeof(Company)).WithMany().HasForeignKey(nameof(CompanyRow.CompanyId))
                    .HasPrincipalKey(nameof(Company.Id)).OnDelete(DeleteBehavior.NoAction);
        }
        modelBuilder.Entity<Company>().HasAlternateKey(c => c.Id);
        modelBuilder.Entity<Company>().ToTable("Company", "app", t =>
            t.HasCheckConstraint("CK_Company_Ownership", "[CompanyId] = [Id]"));
        modelBuilder.Entity<Company>().Property(c => c.Name).HasMaxLength(200);
        modelBuilder.Entity<Employee>(e =>
        {
            e.ToTable("Employee", "bootstrap");
            e.HasKey(u => u.Id);
            e.HasIndex(u => new { u.DirectoryId, u.ObjectId }).IsUnique();
            e.Property(u => u.Name).HasMaxLength(512);
            e.Property(u => u.Email).HasMaxLength(320);
        });
        modelBuilder.Entity<Membership>(e =>
        {
            e.HasIndex(m => new { m.CompanyId, m.UserId }).IsUnique();
            e.Property(m => m.Name).HasMaxLength(512);
            e.Property(m => m.Email).HasMaxLength(320);
            e.HasOne<Employee>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<Team>().Property(t => t.Name).HasMaxLength(200);
        modelBuilder.Entity<Board>(e =>
        {
            e.Property(b => b.Name).HasMaxLength(200);
            e.HasIndex(b => new { b.CompanyId, b.TeamId }).IsUnique();
            e.HasAlternateKey(b => new { b.CompanyId, b.TeamId, b.Id });
            e.HasOne<Team>().WithMany().HasForeignKey(b => new { b.CompanyId, b.TeamId }).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<WorkItem>(e =>
        {
            e.Property(t => t.Title).HasMaxLength(400);
            e.Property(t => t.Description).HasMaxLength(20000);
            e.Property(t => t.Status).HasMaxLength(20);
            e.ToTable("WorkItem", "app", t => t.HasCheckConstraint("CK_Task_Status", "[Status] IN ('todo','inProgress','done')"));
            e.HasIndex(t => new { t.CompanyId, t.TeamId, t.CreatedAt, t.Id });
            e.HasOne<Board>().WithMany().HasForeignKey(t => new { t.CompanyId, t.TeamId, t.BoardId })
                .HasPrincipalKey(b => new { b.CompanyId, b.TeamId, b.Id }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Membership>().WithMany().HasForeignKey(t => new { t.CompanyId, t.AssigneeMembershipId }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Membership>().WithMany().HasForeignKey(t => new { t.CompanyId, t.CreatedBy }).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<CompanyGroup>().Property(g => g.Name).HasMaxLength(200);
        modelBuilder.Entity<GroupMember>(e =>
        {
            e.HasIndex(g => new { g.CompanyId, g.GroupId, g.MembershipId }).IsUnique();
            e.HasOne<CompanyGroup>().WithMany().HasForeignKey(g => new { g.CompanyId, g.GroupId }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Membership>().WithMany().HasForeignKey(g => new { g.CompanyId, g.MembershipId }).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<MemberGrant>(e =>
        {
            e.HasIndex(g => new { g.CompanyId, g.TeamId, g.MembershipId }).IsUnique();
            e.HasOne<Team>().WithMany().HasForeignKey(g => new { g.CompanyId, g.TeamId }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Membership>().WithMany().HasForeignKey(g => new { g.CompanyId, g.MembershipId }).OnDelete(DeleteBehavior.NoAction);
            e.ToTable("MemberGrant", "app", t => t.HasCheckConstraint("CK_MemberGrant_Role", "[Role] BETWEEN 1 AND 3"));
        });
        modelBuilder.Entity<GroupGrant>(e =>
        {
            e.HasIndex(g => new { g.CompanyId, g.TeamId, g.GroupId }).IsUnique();
            e.HasOne<Team>().WithMany().HasForeignKey(g => new { g.CompanyId, g.TeamId }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<CompanyGroup>().WithMany().HasForeignKey(g => new { g.CompanyId, g.GroupId }).OnDelete(DeleteBehavior.NoAction);
            e.ToTable("GroupGrant", "app", t => t.HasCheckConstraint("CK_GroupGrant_Role", "[Role] BETWEEN 1 AND 3"));
        });
        modelBuilder.Entity<Invitation>(e =>
        {
            e.Property(i => i.Email).HasMaxLength(320);
            e.Property(i => i.TokenHash).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasOne<Membership>().WithMany().HasForeignKey(i => new { i.CompanyId, i.InvitedBy }).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<ClientRegistration>(e =>
        {
            e.ToTable("ClientRegistration", "bootstrap");
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(200);
        });
        modelBuilder.Entity<ClientApproval>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.ClientId }).IsUnique();
            e.HasOne<ClientRegistration>().WithMany().HasForeignKey(a => a.ClientId).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<ConnectionGrant>(e =>
        {
            e.HasIndex(g => new { g.CompanyId, g.MembershipId, g.ClientId }).IsUnique();
            e.HasOne<Membership>().WithMany().HasForeignKey(g => new { g.CompanyId, g.MembershipId }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<ClientRegistration>().WithMany().HasForeignKey(g => g.ClientId).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<ConnectionTeam>(e =>
        {
            e.HasIndex(t => new { t.CompanyId, t.ConnectionId, t.TeamId }).IsUnique();
            e.HasOne<ConnectionGrant>().WithMany().HasForeignKey(t => new { t.CompanyId, t.ConnectionId }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Team>().WithMany().HasForeignKey(t => new { t.CompanyId, t.TeamId }).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvent", "app", t => t.HasCheckConstraint("CK_Audit_Actor",
                "([ActorId] IS NOT NULL AND [OperatorObjectId] IS NULL) OR ([ActorId] IS NULL AND [OperatorObjectId] IS NOT NULL)"));
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.CorrelationId).HasMaxLength(128);
            e.HasIndex(a => new { a.CompanyId, a.CreatedAt, a.Id });
            e.HasOne<Membership>().WithMany().HasForeignKey(a => new { a.CompanyId, a.ActorId }).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Team>().WithMany().HasForeignKey(a => new { a.CompanyId, a.TeamId }).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<MutationReceipt>(e =>
        {
            e.Property(r => r.Channel).HasMaxLength(40).IsUnicode(false);
            e.Property(r => r.Operation).HasMaxLength(60).IsUnicode(false);
            e.Property(r => r.Key).HasMaxLength(128).IsUnicode(false);
            e.Property(r => r.RequestHash).HasMaxLength(64).IsUnicode(false);
            e.Property(r => r.ResultVersion).HasMaxLength(32).IsUnicode(false);
            e.HasIndex(r => new { r.CompanyId, r.MembershipId, r.Channel, r.Operation, r.Key }).IsUnique();
            e.HasOne<Membership>().WithMany().HasForeignKey(r => new { r.CompanyId, r.MembershipId }).OnDelete(DeleteBehavior.NoAction);
        });
        modelBuilder.Entity<CompanyCreationReceipt>(e =>
        {
            e.ToTable("CompanyCreationReceipt", "bootstrap");
            e.HasKey(r => new { r.UserId, r.Key });
            e.Property(r => r.Key).HasMaxLength(128).IsUnicode(false);
            e.Property(r => r.RequestHash).HasMaxLength(64).IsUnicode(false);
            e.HasOne<Employee>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).HasPrincipalKey(c => c.Id).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
