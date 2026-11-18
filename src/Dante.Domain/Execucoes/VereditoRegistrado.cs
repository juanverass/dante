using System.Security.Cryptography;
using System.Text;
using Dante.Domain.Comum;
using Dante.Domain.Grupos;

namespace Dante.Domain.Execucoes;

// Veredito estruturado do revisor de um Roteiro (#308, decisão D): registrado pelo caso de uso confiável, a partir da
// ferramenta entregar_resultado do dante_coordenacao, e vinculado ao escopo que o orquestrador fixou para a sessão
// (execução, etapa de revisão, rodada e hash do diff revisado), nunca a um escopo informado pelo agente. Um por etapa
// e imutável (trigger): a mesma entrega repetida é idempotente (mesmo hash do conteúdo); outra é recusada. Resumo e
// itens chegam já conferidos contra credenciais.
public sealed class VereditoRegistrado : EntidadeBase
{
    public const int TamanhoDoHash = 64;
    public const int TamanhoMaximoDoResumo = 2000;
    public const int TamanhoMaximoDoItem = 500;
    public const int MaximoDeItens = 20;
    public const int VersaoDoFormato = 1;

    private VereditoRegistrado()
    {
    }

    public VereditoRegistrado(Execucao execucao, EtapaDaExecucao etapa, string hashDoArtefato, VereditoDeRevisao veredito,
        string resumo, IReadOnlyList<string> itens, string? sessao, DateTimeOffset registradoEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        ArgumentNullException.ThrowIfNull(itens);
        if (execucao.Tipo != TipoDeExecucao.Roteiro || etapa.IdExecucao != execucao.Id ||
            etapa.Passo != TipoDePassoDoRoteiro.Revisar || etapa.Rodada is not { } rodada)
            throw new InvalidOperationException("Veredito só numa etapa de revisão de um Roteiro.");
        if (!Enum.IsDefined(veredito)) throw new ArgumentOutOfRangeException(nameof(veredito), "Veredito inválido.");
        if (hashDoArtefato is not { Length: TamanhoDoHash }) throw new ArgumentException("Hash do artefato inválido.", nameof(hashDoArtefato));
        var texto = resumo?.Trim() ?? "";
        if (texto.Length is 0 or > TamanhoMaximoDoResumo)
            throw new ArgumentException($"O resumo é obrigatório e tem até {TamanhoMaximoDoResumo} caracteres.", nameof(resumo));
        if (itens.Count > MaximoDeItens) throw new ArgumentException($"No máximo {MaximoDeItens} itens.", nameof(itens));
        var lista = new string[itens.Count];
        for (var i = 0; i < itens.Count; i++)
        {
            var item = itens[i]?.Trim() ?? "";
            if (item.Length is 0 or > TamanhoMaximoDoItem)
                throw new ArgumentException($"Cada item tem de 1 a {TamanhoMaximoDoItem} caracteres.", nameof(itens));
            lista[i] = item;
        }
        if (veredito == VereditoDeRevisao.RequerCorrecoes && lista.Length == 0)
            throw new ArgumentException("Pedir correções exige ao menos um item a corrigir.", nameof(itens));
        IdExecucao = execucao.Id;
        IdEtapa = etapa.Id;
        Rodada = rodada;
        HashDoArtefato = hashDoArtefato;
        Veredito = veredito;
        Resumo = texto;
        Itens = Array.AsReadOnly(lista);
        Sessao = sessao;
        HashDoConteudo = CalcularHashDoConteudo(veredito, texto, lista);
        RegistradoEm = registradoEm;
    }

    public Guid IdExecucao { get; private set; }
    public Guid IdEtapa { get; private set; }
    public int Rodada { get; private set; }
    public string HashDoArtefato { get; private set; } = "";
    public VereditoDeRevisao Veredito { get; private set; }
    public string Resumo { get; private set; } = "";
    public IReadOnlyList<string> Itens { get; private set; } = [];
    // Código da sessão (S…) que entregou, para auditoria.
    public string? Sessao { get; private set; }
    public string HashDoConteudo { get; private set; } = "";
    public DateTimeOffset RegistradoEm { get; private set; }

    // Formato versionado: veredito, resumo e itens na ordem entregue, separados por caractere nulo.
    public static string CalcularHashDoConteudo(VereditoDeRevisao veredito, string resumo, IReadOnlyList<string> itens)
    {
        var texto = new StringBuilder("formato=").Append(VersaoDoFormato).Append('\0').Append(veredito).Append('\0')
            .Append(resumo);
        foreach (var item in itens) texto.Append('\0').Append(item);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto.ToString())));
    }
}
