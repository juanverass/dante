namespace Dante.Application.Grupos;

// Cadastro versionado de grupos de agentes (AD-69, #308). Identidade (nome, estado) separada do comportamento (versões
// imutáveis), e versão nova só vale para execuções depois de ativada por decisão humana vinculada ao hash. Recusas de
// entrada viram ArgumentException; de estado (nome em uso, grupo arquivado, participante inválido),
// InvalidOperationException; corrida de gravação, ConflitoDeConcorrenciaException. As mensagens podem ser mostradas
// ao usuário. Nenhuma regra vive no adaptador de interface; remoção física não existe.
public interface ICadastroDeGruposAppService
{
    // Cria o grupo e a v1, ainda não ativada.
    Task<GrupoDeAgentesDto> CriarAsync(NovoGrupoDto novo, CancellationToken cancellationToken = default);
    // Inclui versões e ativações, da mais recente para a mais antiga.
    Task<GrupoDeAgentesDto?> ObterAsync(string nome, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GrupoDeAgentesDto>> ListarAsync(bool incluirArquivados = false, CancellationToken cancellationToken = default);
    // Cria a próxima versão, não ativada; recusa quando nada muda. A versão ativa continua a mesma.
    Task<GrupoDeAgentesDto> EditarAsync(string nome, AlteracaoDoGrupoDto alteracao, long idDoAutor,
        CancellationToken cancellationToken = default);
    // Decisão humana: libera a versão para execuções novas. hashConfirmado é o hash (ou o prefixo exibido, ao menos 12
    // caracteres) que a pessoa viu; os participantes são conferidos de novo contra os agentes atuais.
    Task<GrupoDeAgentesDto> AtivarAsync(string nome, int numeroDaVersao, string hashConfirmado, long idDoAutor,
        CancellationToken cancellationToken = default);
    Task<GrupoDeAgentesDto> DescreverAsync(string nome, string? descricao, CancellationToken cancellationToken = default);
    Task<GrupoDeAgentesDto> DesativarAsync(string nome, CancellationToken cancellationToken = default);
    Task<GrupoDeAgentesDto> ReativarAsync(string nome, CancellationToken cancellationToken = default);
    Task<GrupoDeAgentesDto> ArquivarAsync(string nome, CancellationToken cancellationToken = default);
    // Validação antes de uma execução nova: grupo ativo, versão ativa íntegra e cada participante válido com a versão
    // atual do agente, que é a que a execução congela. Nunca cai para outra versão nem substitui participante.
    Task<ResolucaoDeGrupoDto> ResolverParaExecucaoAsync(string nome, CancellationToken cancellationToken = default);
}
