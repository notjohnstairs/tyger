using Microsoft.EntityFrameworkCore;
using Tyger.Server.Model;

namespace Tyger.Server.Database;

public class TygerDbContext : DbContext
{

    public TygerDbContext(DbContextOptions<TygerDbContext> dbContextOptions)
            : base(dbContextOptions)
    {
    }

    public DbSet<CodespecEntity> Codespecs => Set<CodespecEntity>();
    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<BufferEntity> Buffers => Set<BufferEntity>();
    public DbSet<TagEntity> Tags => Set<TagEntity>();
    public DbSet<TagKeyEntity> TagKeys => Set<TagKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CodespecEntity>(c =>
            {
                c.Property(c => c.Name).IsRequired().UseCollation("C");
                c.Property(c => c.Version).IsRequired();
                c.Property(c => c.CreatedAt).IsRequired().HasDefaultValueSql("(now() AT TIME ZONE 'utc')");
                c.Property(c => c.Spec).IsRequired().HasColumnType("jsonb");
                c.HasNoKey();
            });

        modelBuilder.Entity<RunEntity>(r =>
            {
                r.Property(c => c.Id).ValueGeneratedOnAdd();
                r.Property(c => c.CreatedAt).IsRequired();
                r.Property(c => c.Run).IsRequired().HasColumnType("jsonb");
                r.Property(c => c.Final).HasDefaultValue(false);
                r.Property(c => c.ResourcesCreated).HasDefaultValue(false);
                r.Property(c => c.LogsArchivedAt).HasDefaultValue(null);

                r.HasKey(c => new { c.Id });
                r.HasIndex(c => new { c.CreatedAt, c.Id });
                r.HasIndex(c => new { c.CreatedAt }).HasFilter("resources_created = false");
            });

        modelBuilder.Entity<BufferEntity>(r =>
            {
                r.Property(c => c.Id).IsRequired();
                r.Property(c => c.CreatedAt).IsRequired();
                r.Property(c => c.Etag).IsRequired().HasMaxLength(64);
                r.HasKey(c => new { c.Id });

                r.HasIndex(c => new { c.Id, c.CreatedAt }, "idx_buffers_id_createdAt");
                r.HasIndex(c => new { c.CreatedAt, c.Id }, "idx_buffers_createdAt_id");
            });

        modelBuilder.Entity<TagEntity>(r =>
            {
                r.Property(c => c.Id);
                r.Property(c => c.CreatedAt).IsRequired();
                r.Property(c => c.Key).IsRequired();
                r.Property(c => c.Value).IsRequired().HasMaxLength(256);
                r.HasNoKey();

                r.HasIndex(c => new { c.Key, c.Value, c.CreatedAt, c.Id }, "idx_tags_key_value_createdAt_id");
                r.HasIndex(c => new { c.CreatedAt, c.Id, c.Key, c.Value }, "idx_tags_createdAt_id_key_value");
            });

        modelBuilder.Entity<TagKeyEntity>(r =>
            {
                r.Property(c => c.Id).ValueGeneratedOnAdd();
                r.Property(c => c.Name).IsRequired().HasMaxLength(128);

                r.HasKey(c => new { c.Id });
                r.HasIndex(c => new { c.Name }, "idx_tag_keys_key_name").IsUnique().IncludeProperties("Id");
            });
    }
}

public class CodespecEntity
{
    public string Name { get; set; } = null!;
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Codespec Spec { get; set; } = null!;
}

public class RunEntity
{
    public long Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Run Run { get; set; } = null!;
    public bool Final { get; set; }
    public bool ResourcesCreated { get; set; }
    public DateTimeOffset? LogsArchivedAt { get; set; }
}

public class BufferEntity
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Etag { get; set; } = "";

}

public class TagEntity
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long Key { get; set; }
    public string Value { get; set; } = "";

}

public class TagKeyEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}
