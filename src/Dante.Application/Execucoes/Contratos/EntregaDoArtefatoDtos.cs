using System.Text.RegularExpressions;

namespace Dante.Application.Execucoes;

// Autor do commit da entrega (#311, PR 3): explícito e obrigatório. Vem da configuração do host
// (Orquestracao:Entrega:AutorNome/AutorEmail), nunca do Git do host, do repositório nem do agente.
public sealed record AutorDoCommit(string Nome, string Email)
{
    public const int MaximoDoNome = 100;
    public const int MaximoDoEmail = 254;

    private static readonly Regex FormaDoEmail =
        new("^[^\\s@<>\"]+@[^\\s@<>\"]+\\.[^\\s@<>\"]+$", RegexOptions.CultureInvariant);

    // Nulo quando serve; senão, o motivo. O Git recusa ou corrompe nome com < > e controle; o e-mail segue a forma
    // simples do catálogo (sem espaço nem <>).
    public string? Problema()
    {
        if (string.IsNullOrWhiteSpace(Nome) || Nome != Nome.Trim() || Nome.Length > MaximoDoNome ||
            Nome.Any(c => char.IsControl(c) || c is '<' or '>'))
            return "nome do autor inválido";
        if (string.IsNullOrWhiteSpace(Email) || Email.Length > MaximoDoEmail || !FormaDoEmail.IsMatch(Email))
            return "e-mail do autor inválido";
        return null;
    }
}

// Entrega do artefato aprovado (#311, PR 3), configurada em Orquestracao:Entrega. Sem autor (ausente ou inválido), a
// entrega é recusada antes de qualquer pedido (fail-closed); o motivo diz o que falta, sem o valor.
public sealed record OpcoesDaEntrega(AutorDoCommit? Autor = null, string? ProblemaDaConfiguracao = null)
{
    public static OpcoesDaEntrega De(string? nome, string? email)
    {
        if (string.IsNullOrWhiteSpace(nome) && string.IsNullOrWhiteSpace(email)) return new();
        var autor = new AutorDoCommit(nome ?? "", email ?? "");
        return autor.Problema() is { } problema ? new(null, problema) : new(autor);
    }
}

// O que o commit grava, exatamente como aprovado: a base (HEAD esperado da worktree), o hash do artefato, o autor e a
// mensagem.
public sealed record CommitDoArtefatoDto(string Base, string HashDoArtefato, AutorDoCommit Autor, string Mensagem);

// Desfecho do commit dito pelo Git fechado. Commitado: exatamente um commit novo sobre a base, com a árvore conferida.
// SemEfeito: nenhum commit (o index, se tocado, foi restaurado). Incerto: pode haver commit ou estado parcial; o estado
// observado é relatado e nada é repetido.
public abstract record DesfechoDoCommitDto
{
    private DesfechoDoCommitDto()
    {
    }

    public sealed record Commitado(string Sha) : DesfechoDoCommitDto;

    public sealed record SemEfeito(string Motivo) : DesfechoDoCommitDto;

    public sealed record Incerto(string Motivo, string EstadoObservado) : DesfechoDoCommitDto;
}

public enum ResultadoDaSolicitacaoDeEntrega
{
    // Pedido novo, aguardando a decisão do dono no gateway.
    Solicitada,
    // Já havia um pedido em aberto desta entrega: devolvido (pendente, ou aprovado e agora executado).
    EmAberto,
    // A entrega aprovada foi executada agora (ou tentou executar); o desfecho está em Execucao.
    Executada,
    // Já entregue antes: o commit está no resultado da aprovação.
    JaEntregue,
    NaoEncontrada
}

public sealed record SolicitacaoDeEntregaDto(
    ResultadoDaSolicitacaoDeEntrega Resultado,
    string? Codigo,
    AprovacaoDeAcaoDto? Aprovacao = null,
    ExecucaoDeAcaoDto? Execucao = null);
