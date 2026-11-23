using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Gateway das ferramentas de sessão (AD-72, #311, PR 2). Sem estado e sem banco: a concessão é a do escopo fixado ao
// abrir a sessão (perfil da versão congelada e da etapa), e cada ferramenta continua gravando o próprio efeito pelo seu
// caso de uso, sob a posse da etapa. As ferramentas atuais do dante_coordenacao estão no catálogo com política Nunca:
// passam por aqui sem mudar o que fazem nem as mensagens que devolvem. A recusa de ferramenta desconhecida ou não
// concedida é a mesma ("Ferramenta desconhecida."): o agente não distingue o que existe do que não lhe foi dado.
public sealed class GatewayDeFerramentas : IGatewayDeFerramentas
{
    public const string FerramentaDesconhecida = "Ferramenta desconhecida.";
    public const string SessaoNaoIdentificada = "Sessão não identificada pelo canal; chamada recusada.";
    public const string AcaoProtegida =
        "Esta ação exige aprovação do dono e não é executada por chamada direta de ferramenta.";

    public async Task<T> ChamarAsync<T>(ChamadaDeFerramentaDto chamada, Func<CancellationToken, Task<T>> executar,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chamada);
        ArgumentNullException.ThrowIfNull(executar);
        // 1. Identidade: só a do canal. Sem sessão ou servidor, nada é avaliado.
        var sessao = chamada.Sessao;
        if (sessao is null || string.IsNullOrWhiteSpace(sessao.IdDaSessao) || string.IsNullOrWhiteSpace(sessao.Servidor) ||
            sessao.Concedidas is null)
            throw new ChamadaRecusadaException(SessaoNaoIdentificada);
        // 2. Catálogo fechado: o nome completo é servidor.ferramenta; fora dele, a ação não existe.
        if (string.IsNullOrEmpty(chamada.Ferramenta) || chamada.Ferramenta.Contains('.', StringComparison.Ordinal) ||
            CatalogoDeAcoes.Obter(sessao.Servidor + "." + chamada.Ferramenta) is not { } definicao)
            throw new ChamadaRecusadaException(FerramentaDesconhecida);
        // 3. Concessão do escopo da sessão.
        if (!sessao.Concedidas.Contains(chamada.Ferramenta, StringComparer.Ordinal))
            throw new ChamadaRecusadaException(FerramentaDesconhecida);
        // 4. Schema da ferramenta (forma, argumentos declarados e obrigatórios).
        // Sem schema não há como validar: a chamada é recusada (fail-closed).
        if (chamada.ValidarSchema is null) throw new ChamadaRecusadaException(FerramentaDesconhecida);
        chamada.ValidarSchema(chamada.Argumentos);
        // 5. Política: protegida nunca executa por aqui (fail-closed).
        if (definicao.Protegida) throw new ChamadaRecusadaException(AcaoProtegida);
        // 6. Execução única desta chamada, pelo caso de uso da ferramenta.
        return await executar(cancellationToken);
    }
}
