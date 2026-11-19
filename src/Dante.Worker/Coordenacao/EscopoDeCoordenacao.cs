using Dante.Application.Execucoes;
using Dante.Worker.Execucoes;

namespace Dante.Worker.Coordenacao;

// Escopo do dante_coordenacao numa sessão de delegação (#309, AD-70, PR 3), fixado pelo orquestrador ao abrir a sessão e
// ligado à posse do trabalho da etapa: as gravações das ferramentas passam pela mesma posse (fencing) e pela sessão
// vinculada à etapa. Execução, etapa, dono, canal, repositório e permissões vêm daqui, nunca dos argumentos do agente.
public abstract class EscopoDeCoordenacao
{
    private protected EscopoDeCoordenacao()
    {
    }

    public abstract PerfilDeCoordenacao Perfil { get; }
}

// Etapa de leitura de uma Tarefa que pode delegar: consultar_agente e estado_da_execucao.
public sealed class CoordenacaoDaOrigem(ExecucaoSobPosse controle) : EscopoDeCoordenacao
{
    public override PerfilDeCoordenacao Perfil => PerfilDeCoordenacao.Origem;

    public ExecucaoSobPosse Controle { get; } = controle ?? throw new ArgumentNullException(nameof(controle));

    public ProprietarioDaExecucao Proprietario => new(Controle.Alvo.Canal, Controle.Alvo.IdDoUsuarioNoCanal);
}

// Etapa da subordinada: entregar_resultado.
public sealed class CoordenacaoDaDelegacao(EntregaPelaFerramenta entrega) : EscopoDeCoordenacao
{
    public override PerfilDeCoordenacao Perfil => PerfilDeCoordenacao.Delegacao;

    public EntregaPelaFerramenta Entrega { get; } = entrega ?? throw new ArgumentNullException(nameof(entrega));
}
