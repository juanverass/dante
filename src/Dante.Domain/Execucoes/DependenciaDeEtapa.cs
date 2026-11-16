using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// A etapa só fica pronta depois da requerida. As duas pertencem à mesma execução, e a requerida vem antes na
// numeração: o plano é numerado em ordem topológica, o que torna ciclo impossível por construção.
public sealed class DependenciaDeEtapa : EntidadeBase
{
    private DependenciaDeEtapa()
    {
    }

    public DependenciaDeEtapa(EtapaDaExecucao etapa, EtapaDaExecucao requerida)
    {
        ArgumentNullException.ThrowIfNull(etapa);
        ArgumentNullException.ThrowIfNull(requerida);
        if (etapa.IdExecucao != requerida.IdExecucao)
            throw new InvalidOperationException("Dependência só entre etapas da mesma execução.");
        if (requerida.Numero >= etapa.Numero)
            throw new InvalidOperationException("A etapa requerida precisa vir antes da dependente.");
        IdExecucao = etapa.IdExecucao;
        IdEtapa = etapa.Id;
        IdEtapaRequerida = requerida.Id;
    }

    public Guid IdExecucao { get; private set; }
    public Guid IdEtapa { get; private set; }
    public Guid IdEtapaRequerida { get; private set; }
}
