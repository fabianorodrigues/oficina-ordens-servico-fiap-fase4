# Contrato da API externa de Pagamentos

Status: Pending

Este documento registra as informacoes que ainda precisam ser fornecidas pela API externa de Pagamentos. Nenhum valor abaixo representa contrato oficial.

- [ ] Base URL
- [ ] Rota de solicitacao
- [ ] Metodo HTTP
- [ ] Payload de solicitacao
- [ ] Payload da resposta inicial
- [ ] Campo da URL do webhook
- [ ] Identificador da operacao
- [ ] Idempotencia externa
- [ ] Autenticacao
- [ ] Headers
- [ ] Payload do callback
- [ ] Identificador unico do callback
- [ ] Assinatura ou validacao do webhook
- [ ] Estados possiveis
- [ ] Politica de reenvio
- [ ] Compensacao ou estorno

Pendencia futura de infraestrutura: expor `POST /api/webhooks/payments` no API Gateway com `authorizationType=NONE` somente depois de `ExternalWebhookEnabled=true` e `ContractStatus=Ready`.
