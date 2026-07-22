namespace Oficina.OrdensServico.Domain.Oficina;

public enum TipoManutencao { NaoClassificada = 0, Preventiva = 1, Corretiva = 2 }
public enum StatusOrdemServico { Recebida = 1, EmDiagnostico = 2, AguardandoAprovacao = 3, EmExecucao = 4, Finalizada = 5, Entregue = 6 }
public enum OrigemAtualizacaoStatusOs { Interna = 1, Externa = 2 }
public enum StatusOrcamento { AguardandoAprovacao = 1, Aprovado = 2, Recusado = 3 }
public enum TipoMaterial { Peca = 1, Insumo = 2 }
