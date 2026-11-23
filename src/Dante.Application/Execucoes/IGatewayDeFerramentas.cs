using System.Text.Json;

namespace Dante.Application.Execucoes;

// Identidade de quem chama uma ferramenta do D.A.N.T.E., fixada pelo adaptador a partir do canal exclusivo da sessão
// (pipe e token aleatórios, AD-56), nunca de argumento do agente: a sessão, o servidor que ela recebeu e as ferramentas
// que o escopo dela concede (o perfil derivado da versão congelada e da etapa quando a sessão abriu).
public sealed record SessaoDaFerramentaDto(string IdDaSessao, string Servidor, IReadOnlyCollection<string> Concedidas);

// Uma chamada de ferramenta: a sessão (do canal), o nome curto da ferramenta e os argumentos do agente (dado, nunca
// identidade). O schema é do adaptador que expõe a ferramenta à CLI; o gateway o aplica no seu passo.
public sealed record ChamadaDeFerramentaDto(SessaoDaFerramentaDto Sessao, string Ferramenta, JsonElement Argumentos,
    Action<JsonElement> ValidarSchema);

// Recusa do gateway: texto fixo para o agente, sem dado interno.
public sealed class ChamadaRecusadaException(string motivo) : InvalidOperationException(motivo);

// Gateway das ferramentas do D.A.N.T.E. (AD-72, #311, PR 2). Toda chamada de ferramenta de sessão passa por aqui, na
// ordem: identidade (do canal), catálogo fechado (CatalogoDeAcoes nega o desconhecido), concessão do escopo, schema e
// política. Política Nunca executa uma vez, pelo caso de uso da própria ferramenta; qualquer outra é recusada aqui:
// ação protegida só existe por pedido de aprovação (IAprovacoesDeAcaoAppService), nunca por chamada direta.
public interface IGatewayDeFerramentas
{
    Task<T> ChamarAsync<T>(ChamadaDeFerramentaDto chamada, Func<CancellationToken, Task<T>> executar,
        CancellationToken cancellationToken = default);
}
