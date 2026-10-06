using System.Security.Cryptography;
using System.Text;
using Dante.Domain.Comum;
using Dante.Domain.Conhecimentos;
namespace Dante.Domain.DocumentosFonte;

// Fonte bruta; não possui status de confirmação e não cria Conhecimento.
public sealed class DocumentoFonte : EntidadeBase
{
    private DocumentoFonte() { Origem = Formato = Conteudo = Hash = string.Empty; }
    public DocumentoFonte(Guid idEspaco, Guid? idProjeto, string origem, string formato, string conteudo,
        Sensibilidade sensibilidade, Guid idResponsavel, DateTimeOffset instante)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty) throw new ArgumentException("Escopo inválido.");
        IdEspacoDeConhecimento = idEspaco; IdProjeto = idProjeto;
        Origem = Texto(origem, 500); Formato = Texto(formato, 32).ToLowerInvariant();
        Atualizar(0, conteudo, sensibilidade, idResponsavel, instante);
    }
    public Guid IdEspacoDeConhecimento { get; private set; }
    public Guid? IdProjeto { get; private set; }
    public string Origem { get; private set; }
    public string Formato { get; private set; }
    public string Conteudo { get; private set; }
    public string Hash { get; private set; }
    public Sensibilidade Sensibilidade { get; private set; }
    public int Revisao { get; private set; }
    public Guid IdResponsavel { get; private set; }
    public DateTimeOffset AtualizadoEm { get; private set; }
    public bool Removido { get; private set; }
    public bool Atualizar(int revisaoEsperada, string conteudo, Sensibilidade sensibilidade, Guid responsavel, DateTimeOffset instante)
    {
        if (revisaoEsperada != Revisao) throw new InvalidOperationException("Revisão da fonte mudou; recarregue.");
        if (responsavel == Guid.Empty || !Enum.IsDefined(sensibilidade) || instante == default) throw new ArgumentException("Dados da fonte inválidos.");
        if (string.IsNullOrWhiteSpace(conteudo) || conteudo.Length > 1000000 || conteudo.Contains('\0')) throw new ArgumentException("Fonte textual inválida ou excede o limite.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conteudo)));
        if (!Removido && Hash == hash && Sensibilidade == sensibilidade) return false;
        Conteudo = conteudo; Hash = hash; Sensibilidade = sensibilidade; IdResponsavel = responsavel;
        AtualizadoEm = instante; Revisao++; Removido = false; return true;
    }
    public void Remover(int revisaoEsperada, Guid responsavel, DateTimeOffset instante)
    {
        if (Revisao != revisaoEsperada || Removido) throw new InvalidOperationException("Fonte mudou ou foi removida.");
        if (responsavel == Guid.Empty || instante == default) throw new ArgumentException("Responsável e instante obrigatórios.");
        // Apagar o original e seus índices; provas já selecionadas nos candidatos/conhecimentos permanecem.
        Conteudo = string.Empty; Removido = true; Revisao++; IdResponsavel = responsavel; AtualizadoEm = instante;
    }
    private static string Texto(string valor, int limite) => string.IsNullOrWhiteSpace(valor) || valor.Length > limite || valor.Any(char.IsControl)
        ? throw new ArgumentException("Metadado da fonte inválido.") : valor.Trim();
}
