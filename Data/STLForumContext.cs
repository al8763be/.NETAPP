using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using WebApplication2.Models;

namespace WebApplication2.Data
{
    // Change inheritance to IdentityDbContext<IdentityUser> as per Chapter 6
    public class STLForumContext : IdentityDbContext<IdentityUser>
    {
        public STLForumContext(DbContextOptions<STLForumContext> options) : base(options)
        {
            
        }

        // Your existing DbSets
        public DbSet<Question> Questions { get; set; }
        public DbSet<Answer> Answers { get; set; }
        public DbSet<Like> Likes { get; set; }
        public DbSet<Contest> Contests { get; set; }
        public DbSet<ContestEntry> ContestEntries { get; set; }
        public DbSet<HubSpotDealImport> HubSpotDealImports { get; set; }
        public DbSet<HubSpotOwnerMapping> HubSpotOwnerMappings { get; set; }
        public DbSet<HubSpotSyncState> HubSpotSyncStates { get; set; }
        public DbSet<HubSpotSyncRun> HubSpotSyncRuns { get; set; }

        // Remove the Users DbSet since Identity handles users now
        // public DbSet<User> Users { get; set; } // DELETE THIS LINE

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // IMPORTANT: Call base.OnModelCreating for Identity
            base.OnModelCreating(modelBuilder);

            // Question configuration - Update to use string UserId (Identity uses string IDs)
            modelBuilder.Entity<Question>(entity =>
            {
                entity.HasOne<IdentityUser>()
                      .WithMany()
                      .HasForeignKey(q => q.UserId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Answer configuration  
            modelBuilder.Entity<Answer>(entity =>
            {
                entity.HasOne(a => a.Question)
                      .WithMany(q => q.Answers)
                      .HasForeignKey(a => a.QuestionId)
                      .OnDelete(DeleteBehavior.Cascade);
                      
                entity.HasOne<IdentityUser>()
                      .WithMany()
                      .HasForeignKey(a => a.UserId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Like configuration
            modelBuilder.Entity<Like>(entity =>
            {
                entity.HasOne(l => l.Question)
                      .WithMany()
                      .HasForeignKey(l => l.QuestionId)
                      .OnDelete(DeleteBehavior.Cascade);
                      
                entity.HasOne<IdentityUser>()
                      .WithMany()
                      .HasForeignKey(l => l.UserId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // Contest configuration
            modelBuilder.Entity<Contest>(entity =>
            {
                entity.HasOne<IdentityUser>()
                      .WithMany()
                      .HasForeignKey(c => c.LeaderUserId)
                      .OnDelete(DeleteBehavior.SetNull);
            });

            // ContestEntry configuration
            modelBuilder.Entity<ContestEntry>(entity =>
            {
                entity.HasOne(ce => ce.Contest)
                    .WithMany()
                    .HasForeignKey(ce => ce.ContestId)
                    .OnDelete(DeleteBehavior.Cascade);
          
                entity.HasOne<IdentityUser>()
                    .WithMany()
                    .HasForeignKey(ce => ce.UserId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(ce => new { ce.ContestId, ce.EmployeeNumber }).IsUnique();
            });

            modelBuilder.Entity<HubSpotDealImport>(entity =>
            {
                entity.Property(d => d.ExternalDealId).HasMaxLength(128).IsRequired();
                entity.Property(d => d.HubSpotOwnerId).HasMaxLength(64);
                entity.Property(d => d.OwnerEmail).HasMaxLength(256).IsRequired();
                entity.Property(d => d.OwnerUserId).HasMaxLength(450);
                entity.Property(d => d.DealName).HasMaxLength(512);
                entity.Property(d => d.Amount).HasPrecision(18, 2);
                entity.Property(d => d.SellerProvision).HasPrecision(18, 2);
                entity.Property(d => d.CurrencyCode).HasMaxLength(16);
                entity.Property(d => d.DealStage).HasMaxLength(128);
                entity.Property(d => d.PayloadHash).HasMaxLength(128);

                entity.HasIndex(d => d.ExternalDealId).IsUnique();
                entity.HasIndex(d => new { d.OwnerUserId, d.FulfilledDateUtc });
                entity.HasIndex(d => new { d.HubSpotOwnerId, d.FulfilledDateUtc });

                entity.HasOne(d => d.OwnerUser)
                    .WithMany()
                    .HasForeignKey(d => d.OwnerUserId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(d => d.HubSpotOwner)
                    .WithMany(o => o.FulfilledDeals)
                    .HasForeignKey(d => d.HubSpotOwnerId)
                    .HasPrincipalKey(o => o.HubSpotOwnerId)
                    .OnDelete(DeleteBehavior.NoAction);
            });

            modelBuilder.Entity<HubSpotOwnerMapping>(entity =>
            {
                entity.Property(o => o.HubSpotOwnerId).HasMaxLength(64).IsRequired();
                entity.Property(o => o.HubSpotOwnerEmail).HasMaxLength(256);
                entity.Property(o => o.HubSpotFirstName).HasMaxLength(128);
                entity.Property(o => o.HubSpotLastName).HasMaxLength(128);
                entity.Property(o => o.HubSpotPrimaryTeamName).HasMaxLength(128);
                entity.Property(o => o.HubSpotTeamNames).HasMaxLength(1000);
                entity.Property(o => o.OwnerUserId).HasMaxLength(450);
                entity.Property(o => o.OwnerUsername).HasMaxLength(64);

                entity.HasIndex(o => o.HubSpotOwnerId).IsUnique();
                entity.HasIndex(o => o.OwnerUserId)
                    .IsUnique()
                    .HasFilter("[OwnerUserId] IS NOT NULL");

                entity.HasOne(o => o.OwnerUser)
                    .WithMany()
                    .HasForeignKey(o => o.OwnerUserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<HubSpotSyncState>(entity =>
            {
                entity.Property(s => s.IntegrationName).HasMaxLength(64).IsRequired();
                entity.Property(s => s.LastCursor).HasMaxLength(512);
                entity.Property(s => s.LastError).HasMaxLength(2000);
                entity.HasIndex(s => s.IntegrationName).IsUnique();
            });

            modelBuilder.Entity<HubSpotSyncRun>(entity =>
            {
                entity.Property(r => r.Status).HasMaxLength(32).IsRequired();
                entity.Property(r => r.ErrorMessage).HasMaxLength(2000);
                entity.HasIndex(r => r.StartedUtc);
            });
        }
    }
}
