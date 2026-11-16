namespace Dante.Application.Agentes;

// Cadastro versionado de agentes (AD-68). Identidade (handle, estado) separada do comportamento (versões imutáveis).
// Recusas de entrada viram ArgumentException; de estado (handle em uso, agente arquivado, embutido não editável,
// trabalho ativo), InvalidOperationException; corrida de gravação, ConflitoDeConcorrenciaException. As mensagens podem
// ser mostradas ao usuário. Remoção física não existe: arquivar é o fim do ciclo de vida.
public interface ICadastroDeAgentesAppService
{
    Task<AgenteDto> CriarAsync(NovoAgenteDto novo, CancellationToken cancellationToken = default);
    // Inclui as versões, da mais recente para a mais antiga. Embutidos são descritos sem versão.
    Task<AgenteDto?> ObterAsync(string handle, CancellationToken cancellationToken = default);
    // Embutidos primeiro, depois os registrados por handle.
    Task<IReadOnlyList<AgenteDto>> ListarAsync(bool incluirArquivados = false, CancellationToken cancellationToken = default);
    // Cria a próxima versão; recusa quando nada muda.
    Task<AgenteDto> EditarAsync(string handle, AlteracaoDoAgenteDto alteracao, long idDoAutor,
        CancellationToken cancellationToken = default);
    // Nome e descrição: apresentação, sem versão nova.
    Task<AgenteDto> ApresentarAsync(string handle, string? nome, string? descricao,
        CancellationToken cancellationToken = default);
    Task<AgenteDto> DesativarAsync(string handle, CancellationToken cancellationToken = default);
    Task<AgenteDto> ReativarAsync(string handle, CancellationToken cancellationToken = default);
    // Só desativado e sem execução ou sessão ativa no ledger.
    Task<AgenteDto> ArquivarAsync(string handle, CancellationToken cancellationToken = default);
    // Agente ativo e a versão atual, para congelar numa sessão ou execução nova; nunca cai para outro agente.
    Task<ResolucaoDeAgenteDto> ResolverAsync(string handle, CancellationToken cancellationToken = default);
}
