using Microsoft.EntityFrameworkCore;
using Oficina.OrdensServico.Domain.Oficina;
using Oficina.OrdensServico.Infrastructure.Messaging;
using Oficina.OrdensServico.Infrastructure.Pagamentos;

namespace Oficina.OrdensServico.Infrastructure.Persistencia;

public sealed class OrdensServicoDbContext(DbContextOptions<OrdensServicoDbContext> options) : DbContext(options)
{
    public DbSet<OrdemServico> OrdensServico => Set<OrdemServico>();
    public DbSet<Orcamento> Orcamentos => Set<Orcamento>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PagamentoOrdem> Pagamentos => Set<PagamentoOrdem>();
    public DbSet<SagaOrdemServico> SagasOrdensServico => Set<SagaOrdemServico>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<OrdemServico>(e =>
        {
            e.ToTable("OrdensServico");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClienteId).IsRequired();
            e.Property(x => x.VeiculoId).IsRequired();
            e.Property(x => x.TipoManutencao).HasConversion<int>().IsRequired();
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.OrigemUltimaAtualizacaoStatus).HasConversion<int>().IsRequired();
            e.OwnsOne(x => x.ClienteSnapshot, s =>
            {
                s.Property(x => x.ClienteId).HasColumnName("ClienteSnapshotId");
                s.Property(x => x.Nome).HasColumnName("Nome").HasMaxLength(200);
                s.Property(x => x.Documento).HasColumnName("Documento").HasMaxLength(32);
                s.Property(x => x.Email).HasColumnName("Email").HasMaxLength(200);
                s.Property(x => x.Telefone).HasColumnName("Telefone").HasMaxLength(30);
            });
            e.OwnsOne(x => x.VeiculoSnapshot, s =>
            {
                s.Property(x => x.VeiculoId).HasColumnName("VeiculoSnapshotId");
                s.Property(x => x.Placa).HasColumnName("Placa").HasMaxLength(10);
                s.Property(x => x.Renavam).HasColumnName("Renavam").HasMaxLength(20);
                s.Property(x => x.ModeloDescricao).HasColumnName("ModeloDescricao").HasMaxLength(120);
                s.Property(x => x.Marca).HasColumnName("Marca").HasMaxLength(80);
                s.Property(x => x.Ano).HasColumnName("Ano");
            });
            e.OwnsOne(x => x.Diagnostico, d =>
            {
                d.ToTable("Diagnosticos");
                d.WithOwner().HasForeignKey("OrdemServicoId");
                d.Property<Guid>("OrdemServicoId");
                d.HasKey("OrdemServicoId");
                d.Property(x => x.Descricao).HasMaxLength(2000).IsRequired();
                d.Property(x => x.DataRegistro).IsRequired();
            });
            e.OwnsMany(x => x.ItensServico, i =>
            {
                i.ToTable("ItensServicoOs");
                i.WithOwner().HasForeignKey("OrdemServicoId");
                i.HasKey(x => x.Id);
                i.Property(x => x.ServicoId).IsRequired();
            });
        });

        b.Entity<Orcamento>(e =>
        {
            e.ToTable("Orcamentos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.ValorTotal).HasColumnType("decimal(18,2)").IsRequired();
            e.Property(x => x.TokenAcaoExterna).HasMaxLength(200);
            e.HasIndex(x => x.TokenAcaoExterna).IsUnique();
            e.HasIndex(x => x.OrdemServicoId).IsUnique();
            e.OwnsMany(x => x.ItensServico, i =>
            {
                i.ToTable("OrcamentoItensServico");
                i.WithOwner().HasForeignKey("OrcamentoId");
                i.HasKey(x => x.Id);
                i.Property(x => x.Id).ValueGeneratedNever();
                i.Property(x => x.ValorMaoDeObra).HasColumnType("decimal(18,2)");
                i.Property(x => x.DescricaoSnapshot).HasMaxLength(200);
            });
            e.OwnsMany(x => x.ItensMaterial, i =>
            {
                i.ToTable("OrcamentoItensMaterial");
                i.WithOwner().HasForeignKey("OrcamentoId");
                i.HasKey(x => x.Id);
                i.Property(x => x.Id).ValueGeneratedNever();
                i.Property(x => x.Tipo).HasConversion<int>().IsRequired();
                i.Property(x => x.ValorUnitario).HasColumnType("decimal(18,2)");
                i.Property(x => x.ValorTotal).HasColumnType("decimal(18,2)");
                i.Property(x => x.DescricaoSnapshot).HasMaxLength(200);
                i.HasIndex(x => new { x.Tipo, x.MaterialId });
            });
        });

        b.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.MessageType).HasMaxLength(120).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(120).IsRequired();
            e.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => x.MessageId).IsUnique();
            e.HasIndex(x => new { x.Status, x.LockedUntilUtc, x.ReceivedAtUtc });
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(x => x.Id);
            e.Property(x => x.MessageType).HasMaxLength(120).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(120).IsRequired();
            e.Property(x => x.CausationId).HasMaxLength(120);
            e.Property(x => x.Body).HasColumnType("nvarchar(max)").IsRequired();
            e.Property(x => x.Error).HasMaxLength(500);
            e.HasIndex(x => x.MessageId).IsUnique();
            e.HasIndex(x => new { x.PublishedAtUtc, x.LockedUntilUtc, x.CreatedAtUtc });
        });

        b.Entity<PagamentoOrdem>(e =>
        {
            e.ToTable("Pagamentos");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChaveIdempotencia).HasMaxLength(200).IsRequired();
            e.Property(x => x.PagamentoExternoId).HasMaxLength(120);
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.LockedBy).HasMaxLength(120);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.ChaveIdempotencia).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAtUtc, x.LockedUntilUtc });
            e.HasIndex(x => x.OrdemServicoId);
        });

        b.Entity<SagaOrdemServico>(e =>
        {
            e.ToTable("SagasOrdensServico");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.OrdemServicoId).IsUnique();
        });
    }
}
