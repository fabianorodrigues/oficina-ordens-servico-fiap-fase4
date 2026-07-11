# Kubernetes Ordens

Manifests para publicacao futura do microservico `oficina-ordens-servico` no namespace `oficina`.

## Arquitetura

```text
API Ordens
-> Saga persistida
-> Inbox/Outbox
-> SQS comandos
-> Estoque
-> SQS eventos
-> Saga
```

## Banco e secrets

- Database: `OficinaOrdensServicoDb`
- Runtime user: `ordens_app`
- Migration user: `ordens_migrator`
- Runtime secret: `/oficina/ordens/runtime-db`
- Migration secret: `/oficina/ordens/migration-db`

Os manifests usam Secrets Store CSI e `SecretProviderClass`. Nao ha Kubernetes Secret sincronizado e nao ha connection string versionada.

## Filas

- `oficina-estoque-comandos.fifo`
- `oficina-estoque-comandos-dlq.fifo`
- `oficina-ordens-eventos.fifo`
- `oficina-ordens-eventos-dlq.fifo`

As URLs reais vem do SSM no workflow de deploy e sao renderizadas em ConfigMap temporario.

## Pagamento

Etapa 12 usa provider `Mock` e `Payments__UseMock=true`. O secret futuro `/oficina/payments/mercado-pago` permanece documentado em `config/official.json`, mas nao e montado enquanto o mock estiver ativo.

## Decisoes

- `replicas: 1`
- Strategy `Recreate`
- Consumer concurrency 1
- Sem HPA
- Service `ClusterIP`
- Migration Job antes do Deployment

## Renderizacao local

```powershell
scripts/render-k8s-manifests.ps1 `
  -RuntimeImage oficina-ordens-servico:abcdef1 `
  -MigrationImage oficina-ordens-servico:abcdef1-migration `
  -AwsRegion us-east-1 `
  -CommandsQueueUrl http://localhost:4566/000000000000/oficina-estoque-comandos.fifo `
  -CommandsDlqUrl http://localhost:4566/000000000000/oficina-estoque-comandos-dlq.fifo `
  -EventsQueueUrl http://localhost:4566/000000000000/oficina-ordens-eventos.fifo `
  -EventsDlqUrl http://localhost:4566/000000000000/oficina-ordens-eventos-dlq.fifo `
  -MigrationJobName ordens-migration-local
```
