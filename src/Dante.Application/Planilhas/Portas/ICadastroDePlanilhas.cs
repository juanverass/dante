namespace Dante.Application.Planilhas;

public interface ICadastroDePlanilhas
{
    Task<IReadOnlyList<PlanilhaCadastradaDto>> ListarAsync(CancellationToken cancellationToken = default);

    Task<PlanilhaCadastradaDto?> ObterAsync(string alias, CancellationToken cancellationToken = default);

    // Insere ou substitui pelo alias.
    Task SalvarAsync(PlanilhaCadastradaDto planilha, CancellationToken cancellationToken = default);

    // Executa leitura, validação e mutação sob a mesma exclusão entre processos.
    // O callback é síncrono e recebe o cadastro atual; não deve acessar o store novamente.
    Task<PlanilhaCadastradaDto> AtualizarAsync(string alias,
        Func<IReadOnlyList<PlanilhaCadastradaDto>, PlanilhaCadastradaDto> atualizar,
        CancellationToken cancellationToken = default);

    Task<bool> RemoverAsync(string alias, CancellationToken cancellationToken = default);
}
