# oficina-ordens-servico-fiap-fase4

Microsservico responsavel pela abertura, diagnostico, orcamento e execucao de ordens de servico da Oficina, orquestrando a integracao com Cadastro, Estoque e o provedor de pagamentos.

## Arquitetura

Clean Architecture com 4 camadas:

- `Oficina.OrdensServico.Domain` — entidades e agregados (`OrdemServico`, `Orcamento`); sem dependencias externas.
- `Oficina.OrdensServico.Application` — casos de uso (`OrdensUseCases`), contratos (`Contracts/`), abstracoes (`Abstractions/`) e validators.
- `Oficina.OrdensServico.Infrastructure` — persistencia EF Core, clients HTTP para Cadastro/Estoque, mensageria SQS (Inbox/Outbox) e a saga de pagamento/reserva (`Pagamentos/`).
- `Oficina.OrdensServico.Api` — controllers, autenticacao/autorizacao, middlewares e composition root (`Program.cs`).

## Endpoints principais

- `api/ordens-servico` — abertura, classificacao, diagnostico, status e ciclo de vida da ordem.
- `api/orcamentos` e `api/meus-orcamentos` — aprovacao/recusa de orcamentos (funcionario e cliente).
- `api/minhas-ordens-servico` — consulta pelo proprio cliente.
- `api/orcamentos/acoes-externas` — aprovacao/recusa via link enviado ao cliente (anonimo, por token).
- `api/relatorios` — tempo medio de execucao.
- `api/dev/ordens-servico/{id}/forcar-compensacao` e `/reprocessar-reserva` — apenas em `Development`, para operar a saga manualmente.

## Fluxo distribuido (Saga)

Aprovar um orcamento inicia um fluxo assincrono: pagamento (mock ou API real via `Payments:Mode`) processado por um `BackgroundService`, seguido de reserva de materiais no Estoque via SQS (Outbox local, Inbox no consumidor), com compensacao automatica em caso de recusa. Controlado por `DistributedFlow:Enabled`.

Autenticacao em ambiente local via header scheme (`Authentication:Mode=Development`), bloqueada fora de `Development`.

Na Etapa 15, o modo oficial de publicacao usa `ASPNETCORE_ENVIRONMENT=Production` e exige `Payments:UseMock=true`, `Payments:MockBehavior=Approved`, `Payments:ExternalApiEnabled=false`, `Payments:ExternalWebhookEnabled=false` e `Payments:ContractStatus=Pending`. A integracao real com a API externa de Pagamentos permanece pendente de contrato.

## Pagamentos

O fluxo atual usa `MockPagamentoGateway` por meio de `IPagamentoGateway`. O mock e deterministico, retorna aprovacao com referencia `mock-<chave-idempotencia>` e suporta compensacao idempotente com referencia `mock-compensation-<paymentOperationId>`.

O codigo tambem prepara `ExternalPaymentApiGateway`, `IExternalPaymentContractMapper`, `IPaymentWebhookAuthenticator` e `IPaymentWebhookHandler`. Enquanto o contrato externo estiver pendente, o mapper e o autenticador concretos permanecem em modo `Pending` e a rota `POST /api/webhooks/payments` retorna `404` quando `ExternalWebhookEnabled=false`.

## Publicacao EKS preparada

Artefatos adicionados para publicacao independente:

- `config/official.json` com dados nao sensiveis da configuracao oficial.
- `Dockerfile` para a imagem runtime sem SDK.
- `Dockerfile.migration` para EF Migration Bundle.
- `deploy/k8s/` com ServiceAccounts, SecretProviderClasses, ConfigMap, Service, Deployment `Recreate` com uma replica e Migration Job.
- `scripts/validate-official-config.ps1`, `scripts/render-k8s-manifests.ps1`, `scripts/validate-ordens-deployment.ps1`, `scripts/smoke-test.ps1` e `scripts/run-saga-smoke-test.ps1`.
- Workflows `Ordens CI` e `Ordens Deploy`.

Banco oficial:

- Database: `OficinaOrdensServicoDb`
- Runtime user: `ordens_app`
- Migration user: `ordens_migrator`
- Secrets: `/oficina/ordens/runtime-db` e `/oficina/ordens/migration-db`

As senhas e connection strings nao sao versionadas neste repositorio. Em Production a connection string e lida via Secrets Store CSI em `/mnt/secrets-store` com `AddKeyPerFile`, usando a chave `ConnectionStrings__DefaultConnection`.

Filas oficiais:

- Comandos para Estoque: `oficina-estoque-comandos.fifo`
- DLQ de comandos: `oficina-estoque-comandos-dlq.fifo`
- Eventos para Ordens: `oficina-ordens-eventos.fifo`
- DLQ de eventos: `oficina-ordens-eventos-dlq.fifo`

Decisoes preservadas: uma replica, strategy `Recreate`, consumer concurrency 1, sem HPA, Inbox, Outbox, Saga persistida, snapshots de Saga, compensacao e pagamento mock.

Execucao futura:

```text
GitHub -> Actions -> Ordens Deploy -> Run workflow -> main -> DEPLOY
```

Validacoes AWS pendentes ate o acesso AWS Academy voltar: STS, SSM real, ECR real, Secrets Manager metadata, SQS real, redrive policies, Pod Identity/IRSA, CSI/ASCP, push de imagens, Migration Job, migration real, deployment EKS, consumer SQS real, Outbox real distribuido, compensacao real entre servicos, DLQ real, health/readiness no cluster e smoke test.

## CI/CD

- CI principal: `Ordens CI`, executada em todo Pull Request para `main`.
- Required check esperado na branch protection: `Ordens CI`.
- Workflow manual: `Ordens Deploy`, executado somente por `workflow_dispatch` na `main`, com confirmation `DEPLOY`.
- Repository Secrets usados pelo deploy: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`.
- Repository Variables usadas pelo deploy: `AWS_REGION`.
- A CI nao recebe credenciais AWS, nao publica imagens e nao altera AWS ou Kubernetes.
- O deploy valida configuracao oficial, pagamento mock aprovado, integracao externa desabilitada, webhook externo desabilitado, SSM, SQS, Secrets Manager por metadata, ECR, migration, rollout, health e readiness.
- Nao existe pipeline dedicada de rollback ou destroy. Para corrigir uma entrega, reverta ou ajuste o codigo em nova branch, abra Pull Request, aguarde `Ordens CI`, faca merge na `main` e execute novamente `Ordens Deploy`.
- Branch protection recomendada: Pull Request obrigatorio, required check `Ordens CI`, bloqueio de force push e bloqueio de delecao da branch. Segundo revisor nao e obrigatorio para execucao individual ou dupla.

## Ambiente local (Docker Compose)

Este repositorio contem o `docker-compose.local.yml` que orquestra os tres servicos (Cadastro, Estoque, Ordens de Servico), SQL Server, LocalStack (SQS) e um mock de pagamentos (WireMock).

```powershell
Copy-Item .env.local.example .env.local   # ou scripts/setup-local-env.ps1
scripts/start-local.ps1
scripts/status-local.ps1
scripts/smoke-local.ps1
scripts/stop-local.ps1
```

## Build e testes locais

```powershell
dotnet build src\Oficina.OrdensServico.Api\Oficina.OrdensServico.Api.csproj
dotnet test
```

Os testes end-to-end (`Oficina.EndToEndTests`) exigem o ambiente Docker Compose ativo e a variavel `RUN_E2E=true`; rodam via `docker compose --profile tests run --rm e2e-tests`.
