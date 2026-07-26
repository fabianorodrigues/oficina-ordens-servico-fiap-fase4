namespace Oficina.OrdensServico.Application.Abstractions;

/// <summary>
/// Metricas de negocio vistas pela camada de aplicacao.
/// A abstracao existe porque o Meter vive na Infrastructure e a Application nao
/// referencia Infrastructure. Sao sinais operacionais best-effort: o banco e os
/// SagaSnapshots continuam sendo a fonte oficial dos estados.
/// </summary>
public interface IOrdensBusinessMetrics
{
    void OrdemCriada();

    /// <summary>
    /// integration e operation precisam vir de conjuntos fechados: texto livre em
    /// dimensao de metrica explode a cardinalidade.
    /// </summary>
    void FalhaIntegracao(string integration, string operation);
}
