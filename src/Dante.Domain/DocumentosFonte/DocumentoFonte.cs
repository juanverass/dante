using System.Security.Cryptography;
using System.Text;
using Dante.Domain.Comum;
using Dante.Domain.Conhecimentos;
namespace Dante.Domain.DocumentosFonte;

// Fonte bruta; não possui status de confirmação e não cria Conhecimento.
public sealed class DocumentoFonte : EntidadeBase
{
    private DocumentoFonte() { Origem = Formato = Conteudo = Hash = string.Empty; }
    public const int TamanhoMaximoDoTitulo = 200;
    public const int TamanhoMaximoDaJustificativa = 2000;
    public const int TamanhoMaximoDaProveniencia = 500;
    public const int QuantidadeMaximaDeTags = 50;
    public const int TamanhoMaximoDaTag = 100;
    // Metadados opcionais (#257) descrevem o registro; não fazem parte do conteúdo nem do hash.
    public DocumentoFonte(Guid idEspaco, Guid? idProjeto, string origem, string formato, string conteudo,
        AutoriaDaFonte autoria, Sensibilidade sensibilidade, Guid idResponsavel, DateTimeOffset instante,
        string? titulo = null, IReadOnlyList<string>? tags = null, string? justificativa = null, string? proveniencia = null)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty) throw new ArgumentException("Escopo inválido.");
        IdEspacoDeConhecimento = idEspaco; IdProjeto = idProjeto;
        Origem = Texto(origem, 500); Formato = Texto(formato, 32).ToLowerInvariant();
        Titulo = titulo is null ? null : Texto(titulo, TamanhoMaximoDoTitulo);
        Tags = NormalizarTags(tags);
        Justificativa = justificativa is null ? null : TextoLivre(justificativa, TamanhoMaximoDaJustificativa);
        Proveniencia = proveniencia is null ? null : Texto(proveniencia, TamanhoMaximoDaProveniencia);
        Atualizar(0, conteudo, autoria, sensibilidade, idResponsavel, instante);
    }
    public Guid IdEspacoDeConhecimento { get; private set; }
    public Guid? IdProjeto { get; private set; }
    public string Origem { get; private set; }
    public string Formato { get; private set; }
    public string Conteudo { get; private set; }
    public string Hash { get; private set; }
    public Sensibilidade Sensibilidade { get; private set; }
    // Autoria do conteúdo vigente; fontes anteriores à #264 permanecem Desconhecida até nova importação.
    public AutoriaDaFonte Autoria { get; private set; }
    // Fontes anteriores à #257 e importações sem metadados ficam com valores neutros (null/vazio).
    public string? Titulo { get; private set; }
    public IReadOnlyList<string> Tags { get; private set; } = Array.AsReadOnly(Array.Empty<string>());
    public string? Justificativa { get; private set; }
    // Quem registrou (ex.: MCP/agente:...;sessao:<instância>:<sessão>, #249); não é a autoria do conteúdo.
    public string? Proveniencia { get; private set; }
    public int Revisao { get; private set; }
    public Guid IdResponsavel { get; private set; }
    public DateTimeOffset AtualizadoEm { get; private set; }
    public bool Removido { get; private set; }
    public bool Atualizar(int revisaoEsperada, string conteudo, AutoriaDaFonte autoria, Sensibilidade sensibilidade, Guid responsavel, DateTimeOffset instante)
    {
        if (revisaoEsperada != Revisao) throw new InvalidOperationException("Revisão da fonte mudou; recarregue.");
        if (responsavel == Guid.Empty || !Enum.IsDefined(sensibilidade) || instante == default) throw new ArgumentException("Dados da fonte inválidos.");
        if (autoria is not (AutoriaDaFonte.Usuario or AutoriaDaFonte.Agente)) throw new ArgumentException("Autoria da fonte obrigatória.");
        if (string.IsNullOrWhiteSpace(conteudo) || conteudo.Length > 1000000 || conteudo.Contains('\0')) throw new ArgumentException("Fonte textual inválida ou excede o limite.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conteudo)));
        if (!Removido && Hash == hash && Sensibilidade == sensibilidade && Autoria == autoria) return false;
        Conteudo = conteudo; Hash = hash; Sensibilidade = sensibilidade; Autoria = autoria; IdResponsavel = responsavel;
        AtualizadoEm = instante; Revisao++; Removido = false; return true;
    }
    public void Remover(int revisaoEsperada, Guid responsavel, DateTimeOffset instante)
    {
        if (Revisao != revisaoEsperada || Removido) throw new InvalidOperationException("Fonte mudou ou foi removida.");
        if (responsavel == Guid.Empty || instante == default) throw new ArgumentException("Responsável e instante obrigatórios.");
        // Apagar o original e seus índices; provas já selecionadas nos candidatos/conhecimentos permanecem.
        Conteudo = string.Empty; Removido = true; Revisao++; IdResponsavel = responsavel; AtualizadoEm = instante;
    }
    // Mesma regra de tags do Conhecimento: aparadas, sem repetição ignorando maiúsculas, até 50 de 100 caracteres.
    private static IReadOnlyList<string> NormalizarTags(IReadOnlyList<string>? tags)
    {
        var normalizadas = new List<string>();
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Trim().Length > TamanhoMaximoDaTag || tag.Any(char.IsControl)) throw new ArgumentException("Tag inválida.");
            if (!normalizadas.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase)) normalizadas.Add(tag.Trim());
            if (normalizadas.Count > QuantidadeMaximaDeTags) throw new ArgumentException("Quantidade de tags excede o limite.");
        }
        return normalizadas.AsReadOnly();
    }
    private static string TextoLivre(string valor, int limite) => string.IsNullOrWhiteSpace(valor) || valor.Length > limite || valor.Contains('\0')
        ? throw new ArgumentException("Metadado da fonte inválido.") : valor.Trim();
    private static string Texto(string valor, int limite) => string.IsNullOrWhiteSpace(valor) || valor.Length > limite || valor.Any(char.IsControl)
        ? throw new ArgumentException("Metadado da fonte inválido.") : valor.Trim();
}
