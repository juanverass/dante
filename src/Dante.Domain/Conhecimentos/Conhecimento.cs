using System.Text.Json;
using Dante.Domain.Comum;

namespace Dante.Domain.Conhecimentos;

public sealed class Conhecimento : EntidadeBase
{
    public const int TamanhoMaximoDoConteudo = 100000;
    public const int QuantidadeMaximaDeTags = 50;
    private readonly List<RevisaoDoConhecimento> historico = [];

    private Conhecimento() { }

    public Conhecimento(Guid idEspacoDeConhecimento, Guid? idProjeto, TipoDeConhecimento tipo,
        string? conteudo, string? dadosEstruturados, StatusDoConhecimento status, double? confianca,
        Sensibilidade sensibilidade, DateTimeOffset? validoDesde, DateTimeOffset? validoAte,
        IEnumerable<string>? tags, ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        if (idEspacoDeConhecimento == Guid.Empty || idProjeto == Guid.Empty)
            throw new ArgumentException("Escopo de conhecimento inválido.");
        if (status is not (StatusDoConhecimento.Inferido or StatusDoConhecimento.Temporario))
            throw new ArgumentException("Conhecimento novo exige confirmação explícita.", nameof(status));
        ArgumentNullException.ThrowIfNull(proveniencia);
        ValidarInstante(instante);
        var dados = ValidarDados(tipo, conteudo, dadosEstruturados, confianca, sensibilidade, validoDesde, validoAte, tags);
        IdEspacoDeConhecimento = idEspacoDeConhecimento;
        IdProjeto = idProjeto;
        IdAutor = proveniencia.IdResponsavel;
        CriadoEm = AtualizadoEm = instante;
        Status = status;
        AplicarDados(dados);
        Registrar(proveniencia, instante);
    }

    public Guid IdEspacoDeConhecimento { get; private set; }
    public Guid? IdProjeto { get; private set; }
    public Guid IdAutor { get; private set; }
    public TipoDeConhecimento Tipo { get; private set; }
    public string? Conteudo { get; private set; }
    public string? DadosEstruturados { get; private set; }
    public StatusDoConhecimento Status { get; private set; }
    public double? Confianca { get; private set; }
    public Sensibilidade Sensibilidade { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset AtualizadoEm { get; private set; }
    public DateTimeOffset? ValidoDesde { get; private set; }
    public DateTimeOffset? ValidoAte { get; private set; }
    public IReadOnlyList<string> Tags { get; private set; } = Array.AsReadOnly(Array.Empty<string>());
    public int Revisao => historico.Count;
    public IReadOnlyList<RevisaoDoConhecimento> Historico => historico.AsReadOnly();
    public ProvenienciaDoConhecimento Proveniencia => historico[^1].Proveniencia;
    public Guid? IdConhecimentoSubstituto { get; private set; }

    // Validade temporal não equivale a confirmação. Intervalo [desde, até), quando definido.
    public bool EstaValidoEm(DateTimeOffset instante) =>
        Status is not (StatusDoConhecimento.Substituido or StatusDoConhecimento.Inativo) &&
        (ValidoDesde is null || instante >= ValidoDesde) && (ValidoAte is null || instante < ValidoAte);

    public void Confirmar(int revisaoEsperada, ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        GarantirAlteravel(revisaoEsperada, proveniencia, instante);
        if (Tipo == TipoDeConhecimento.Inferencia)
            throw new InvalidOperationException("Inferência não pode ser confirmada; corrija a classificação com evidência antes.");
        if (Status == StatusDoConhecimento.Confirmado)
            throw new InvalidOperationException("Conhecimento já confirmado.");
        Status = StatusDoConhecimento.Confirmado;
        Registrar(proveniencia, instante);
    }

    public void Corrigir(int revisaoEsperada, TipoDeConhecimento tipo, string? conteudo, string? dadosEstruturados,
        double? confianca, Sensibilidade sensibilidade, DateTimeOffset? validoDesde, DateTimeOffset? validoAte,
        IEnumerable<string>? tags, ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        GarantirAlteravel(revisaoEsperada, proveniencia, instante);
        var dados = ValidarDados(tipo, conteudo, dadosEstruturados, confianca, sensibilidade, validoDesde, validoAte, tags);
        AplicarDados(dados);
        // A confirmação de uma versão não confirma automaticamente seu novo conteúdo.
        if (Status == StatusDoConhecimento.Confirmado) Status = StatusDoConhecimento.Inferido;
        Registrar(proveniencia, instante);
    }

    public void Invalidar(int revisaoEsperada, ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        GarantirAlteravel(revisaoEsperada, proveniencia, instante);
        Status = StatusDoConhecimento.Inativo;
        Registrar(proveniencia, instante);
    }

    public void SubstituirPor(Conhecimento substituto, int revisaoEsperada,
        ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        GarantirAlteravel(revisaoEsperada, proveniencia, instante);
        ArgumentNullException.ThrowIfNull(substituto);
        if (substituto.Id == Id || substituto.IdEspacoDeConhecimento != IdEspacoDeConhecimento ||
            substituto.IdProjeto != IdProjeto || substituto.Status is StatusDoConhecimento.Substituido or StatusDoConhecimento.Inativo)
            throw new ArgumentException("Substituto deve ser outro conhecimento ativo no mesmo escopo.", nameof(substituto));
        IdConhecimentoSubstituto = substituto.Id;
        Status = StatusDoConhecimento.Substituido;
        Registrar(proveniencia, instante);
    }

    // Consolidação explícita mantém o conteúdo/status e incorpora todas as fontes das revisões originais.
    public void IncorporarFontesDe(IReadOnlyList<Conhecimento> duplicatas, int revisaoEsperada,
        ProvenienciaDoConhecimento responsavel, DateTimeOffset instante)
    {
        GarantirAlteravel(revisaoEsperada, responsavel, instante);
        if (duplicatas.Count is < 1 or > 20) throw new ArgumentException("Consolidação exige de uma a vinte fontes.");
        foreach (var fonte in duplicatas)
        {
            if (fonte.Id == Id || fonte.IdEspacoDeConhecimento != IdEspacoDeConhecimento || fonte.IdProjeto != IdProjeto ||
                fonte.Sensibilidade > Sensibilidade || fonte.Status == StatusDoConhecimento.Confirmado && Status != StatusDoConhecimento.Confirmado ||
                fonte.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)
                throw new ArgumentException("Consolidação exige fontes ativas e compatíveis no mesmo escopo.");
            foreach (var revisao in fonte.Historico)
                if (revisao.Sensibilidade > Sensibilidade) throw new ArgumentException("Consolidação não reduz sensibilidade de fontes históricas.");
        }
        var fontes = duplicatas.SelectMany(x => x.Historico.Select(r => r.Proveniencia)).Distinct().ToArray();
        var anteriores = historico.Select(x => x.Proveniencia).ToHashSet();
        foreach (var fonte in fontes) if (anteriores.Add(fonte)) Registrar(fonte, instante);
        Registrar(responsavel, instante);
    }

    private void GarantirAlteravel(int revisaoEsperada, ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        if (revisaoEsperada != Revisao) throw new InvalidOperationException("Revisão do conhecimento desatualizada.");
        if (Status is StatusDoConhecimento.Substituido or StatusDoConhecimento.Inativo)
            throw new InvalidOperationException("Conhecimento substituído ou inativo não pode ser alterado.");
        ArgumentNullException.ThrowIfNull(proveniencia);
        ValidarInstante(instante);
        if (instante < AtualizadoEm) throw new ArgumentException("Timestamp anterior à última revisão.", nameof(instante));
    }

    private void Registrar(ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        AtualizadoEm = instante;
        historico.Add(new RevisaoDoConhecimento(Revisao + 1, Tipo, Conteudo, DadosEstruturados, Status, Confianca,
            Sensibilidade, ValidoDesde, ValidoAte, Tags, proveniencia, instante, IdConhecimentoSubstituto));
    }

    private static void ValidarInstante(DateTimeOffset instante)
    {
        if (instante == default) throw new ArgumentException("Timestamp é obrigatório.", nameof(instante));
    }

    private sealed record Dados(TipoDeConhecimento Tipo, string? Conteudo, string? Estruturados,
        double? Confianca, Sensibilidade Sensibilidade, DateTimeOffset? Desde, DateTimeOffset? Ate, IReadOnlyList<string> Tags);

    private static Dados ValidarDados(TipoDeConhecimento tipo, string? conteudo, string? estruturados,
        double? confianca, Sensibilidade sensibilidade, DateTimeOffset? desde, DateTimeOffset? ate, IEnumerable<string>? tags)
    {
        if (!Enum.IsDefined(tipo) || !Enum.IsDefined(sensibilidade)) throw new ArgumentException("Classificação inválida.");
        if (confianca is { } valor && (!double.IsFinite(valor) || valor < 0 || valor > 1))
            throw new ArgumentOutOfRangeException(nameof(confianca), "Confiança deve estar entre zero e um.");
        conteudo = string.IsNullOrWhiteSpace(conteudo) ? null : conteudo.Trim();
        estruturados = string.IsNullOrWhiteSpace(estruturados) ? null : estruturados.Trim();
        if (conteudo is null && estruturados is null) throw new ArgumentException("Conhecimento exige conteúdo ou dados estruturados.");
        if (conteudo?.Length > TamanhoMaximoDoConteudo || estruturados?.Length > TamanhoMaximoDoConteudo)
            throw new ArgumentException("Conteúdo excede o limite.");
        if (estruturados is not null)
        {
            try
            {
                using var documento = JsonDocument.Parse(estruturados);
                if (documento.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Dados estruturados devem ser um objeto JSON.");
            }
            catch (JsonException) { throw new ArgumentException("Dados estruturados inválidos."); }
        }
        if (desde == default(DateTimeOffset) || ate == default(DateTimeOffset) || (desde is not null && ate is not null && ate <= desde))
            throw new ArgumentException("Intervalo de validade inválido.");
        var normalizadas = new List<string>();
        foreach (var tag in tags ?? [])
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Trim().Length > 100 || tag.Any(char.IsControl))
                throw new ArgumentException("Tag inválida.", nameof(tags));
            if (!normalizadas.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase)) normalizadas.Add(tag.Trim());
            if (normalizadas.Count > QuantidadeMaximaDeTags) throw new ArgumentException("Quantidade de tags excede o limite.");
        }
        return new Dados(tipo, conteudo, estruturados, confianca, sensibilidade, desde, ate, normalizadas.AsReadOnly());
    }

    private void AplicarDados(Dados dados)
    {
        Tipo = dados.Tipo; Conteudo = dados.Conteudo; DadosEstruturados = dados.Estruturados;
        Confianca = dados.Confianca; Sensibilidade = dados.Sensibilidade;
        ValidoDesde = dados.Desde; ValidoAte = dados.Ate; Tags = dados.Tags;
    }
}
