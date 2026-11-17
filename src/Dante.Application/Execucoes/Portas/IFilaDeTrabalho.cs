using Dante.Domain.Execucoes;

namespace Dante.Application.Execucoes;

// Fila durável no PostgreSQL (AD-67). Contrato para produtores e para o processador:
//
// - Produtor (caso de uso): cria execução/etapa pelos métodos do domínio, chama EnfileirarAsync e depois
//   IUnitOfWorkDaOrquestracao.SalvarAlteracoesAsync: execução, etapa, evento e trabalho entram numa única transação
//   (outbox). Chave de deduplicação repetida faz o salvamento inteiro falhar com ConflitoDeConcorrenciaException, e a
//   execução não é criada; o chamador consulta ObterPorChaveAsync num escopo novo para achar a existente. A notificação
//   (LISTEN/NOTIFY) sai do banco só no commit; o processador também faz polling, então notificação perdida só atrasa.
// - Processador: ReivindicarAsync (FOR UPDATE SKIP LOCKED, geração + 1, lease), RenovarAsync a cada heartbeat e
//   gravações só por GravarComPosseAsync. Nenhum destes métodos mantém transação aberta depois de retornar: a CLI roda
//   sempre fora de transação.
//
// Agora é o relógio da fila: lease, disponibilidade e carimbos usam este instante (controlável nos testes).
public interface IFilaDeTrabalho
{
    DateTimeOffset Agora { get; }

    Task EnfileirarAsync(TrabalhoDaFila trabalho, CancellationToken cancellationToken = default);

    // Leitura sem rastreamento; nulo quando a chave nunca foi enfileirada.
    Task<TrabalhoDaFila?> ObterPorChaveAsync(string chaveDeDeduplicacao, CancellationToken cancellationToken = default);

    // Tudo ou nada numa transação do ledger: para casos de uso que reutilizam gravações que já salvam (o job legado
    // registrado pelo ledger da #299) e precisam do trabalho na mesma transação.
    Task ExecutarEmTransacaoAsync(Func<CancellationToken, Task> gravacao, CancellationToken cancellationToken = default);

    // O próximo trabalho pendente e disponível, ou reivindicado com lease vencido; nulo quando não há.
    Task<TrabalhoReivindicado?> ReivindicarAsync(IdentidadeDoProcessador processador, TimeSpan lease,
        CancellationToken cancellationToken = default);

    // Heartbeat: estende o lease só se a posse ainda vale. Falso: posse perdida.
    Task<bool> RenovarAsync(PosseDoTrabalho posse, TimeSpan lease, CancellationToken cancellationToken = default);

    // Grava sob posse, numa transação com a linha do trabalho travada (FOR UPDATE) e posse conferida dentro dela:
    // geração, dono, estado reivindicado e lease não vencido. Recusa com PosseDoTrabalhoPerdidaException sem gravar
    // nada. encerrarComo nulo mantém o trabalho reivindicado (início de etapa); Pendente devolve à fila disponível em
    // disponivelEm (retomada automática), os demais encerram o trabalho na mesma transação da gravação.
    Task GravarComPosseAsync(PosseDoTrabalho posse, Func<CancellationToken, Task>? gravacao,
        EstadoDoTrabalho? encerrarComo, string? motivo = null, DateTimeOffset? disponivelEm = null,
        CancellationToken cancellationToken = default);

    // Devolve à fila sem resultado, para nova tentativa a partir de disponivelEm (backoff ou slot ocupado).
    // contarTentativa=false desfaz o incremento da reivindicação. Quem decide desistir ao esgotar as tentativas é o
    // processador, que encerra a execução sob posse. Falso: posse perdida.
    Task<bool> DevolverAsync(PosseDoTrabalho posse, DateTimeOffset disponivelEm, string? erro, bool contarTentativa,
        CancellationToken cancellationToken = default);

    // Instante em que o próximo trabalho fica reivindicável (timer, backoff ou lease a vencer); nulo sem trabalho.
    // O processador dorme até ele, sem busy loop.
    Task<DateTimeOffset?> ProximaDisponibilidadeAsync(CancellationToken cancellationToken = default);

    // Recuperação na inicialização: devolve à fila, disponíveis já, os trabalhos reivindicados por outro boot deste host
    // (morto: o Worker é instância única no host). A geração não muda; a próxima reivindicação a incrementa, e o boot
    // antigo, se ainda existir, perde a posse. Retorna quantos foram devolvidos.
    Task<int> DevolverDeBootsAnterioresAsync(IdentidadeDoProcessador atual, CancellationToken cancellationToken = default);
}
