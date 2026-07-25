#language: pt
Funcionalidade: Saga distribuida de ordem de servico
  Como a oficina
  Quero que a aprovacao de um orcamento reserve materiais no Estoque por mensageria
  Para que a ordem so entre em execucao com os materiais garantidos

  O fluxo exercitado e Ordens -> SQS -> Estoque -> SQS -> Ordens, com as APIs
  reais dos tres microsservicos, SQL Server e filas FIFO. O pagamento usa o
  mesmo mock do ambiente publicado, com retorno Approved.

  Contexto:
    Dado um catalogo com uma peca com saldo de 10 unidades
    E um servico de catalogo que consome 2 unidades dessa peca
    E uma ordem de servico aberta com diagnostico registrado

  Cenario: Reserva confirmada leva a ordem para execucao
    Quando o orcamento e aprovado
    Entao o saldo disponivel da peca passa a ser 8 unidades
    E a ordem de servico fica com status "EmExecucao"
    E a saga registra a reserva confirmada pelo Estoque
    E a saga da ordem fica no estado "Concluida"

  Cenario: Reentrega do mesmo evento nao produz efeito duplicado
    Quando o orcamento e aprovado
    E o saldo disponivel da peca passa a ser 8 unidades
    E o mesmo evento de reserva confirmada e reentregue
    Entao o saldo disponivel da peca continua em 8 unidades
    E a ordem de servico permanece com status "EmExecucao"
    E o Inbox de Ordens registra uma unica mensagem processada para o evento

  Cenario: Compensacao devolve o saldo reservado
    Dado que o orcamento foi aprovado e a reserva confirmada
    Quando a compensacao da ordem e solicitada
    Entao o saldo disponivel da peca volta para 10 unidades
    E a saga da ordem fica no estado "Compensada"

  Cenario: Orcamento recusado nao reserva material
    Quando o orcamento e recusado
    Entao o saldo disponivel da peca continua em 10 unidades
    E a ordem de servico fica com status "Finalizada"
    E nenhuma saga foi iniciada para a ordem
