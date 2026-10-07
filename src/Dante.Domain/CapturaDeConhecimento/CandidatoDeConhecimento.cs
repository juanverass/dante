using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dante.Domain.Comum;
using Dante.Domain.Conhecimentos;
namespace Dante.Domain.CapturaDeConhecimento;

public enum NaturezaDoConteudo { DitoPeloUsuario, ConclusaoDoAgente, FonteSelecionada }
public enum ModoDeCaptura { Explicita, SugestaoAutomatica }
public enum EstadoDoCandidato { Pendente, Promovido, Rejeitado, Descartado }
public sealed record AtoDoCandidato(int Revisao, string Acao, TipoDeConhecimento Tipo, string Conteudo,
    Sensibilidade Sensibilidade, string Justificativa, ProvenienciaDoConhecimento Proveniencia,
    DateTimeOffset Instante, EstadoDoCandidato Estado, Guid? IdConhecimento, string? Titulo = null, IReadOnlyList<string>? Tags = null);

public sealed class CandidatoDeConhecimento : EntidadeBase
{
    private readonly List<AtoDoCandidato> historico = [];
    private CandidatoDeConhecimento() { }
    public CandidatoDeConhecimento(Guid idEspaco, Guid? idProjeto, TipoDeConhecimento tipo, string conteudo,
        Sensibilidade sensibilidade, NaturezaDoConteudo natureza, ModoDeCaptura modo, string justificativa,
        ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante, Guid? idIncidente = null, Guid? idSolucao = null, string? titulo = null, IReadOnlyList<string>? tags = null)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty || !Enum.IsDefined(natureza) || !Enum.IsDefined(modo) || !Enum.IsDefined(tipo))
            throw new ArgumentException("Escopo ou origem da captura inválido.");
        if ((idIncidente is null) != (idSolucao is null) || idIncidente == Guid.Empty || idSolucao == Guid.Empty)
            throw new ArgumentException("Consolidação exige incidente e solução.");
        IdEspacoDeConhecimento = idEspaco; IdProjeto = idProjeto; Natureza = natureza; Modo = modo;
        IdIncidente = idIncidente; IdSolucao = idSolucao;
        // Conclusão do agente não ganha autoridade de fato pela classificação/confiança sugerida.
        if (natureza == NaturezaDoConteudo.ConclusaoDoAgente) tipo = TipoDeConhecimento.Inferencia;
        Aplicar(tipo, conteudo, sensibilidade, justificativa, proveniencia, instante);
        ValidarMetadados(titulo, tags);
        Registrar("capturado", proveniencia, instante, titulo, tags);
    }
    public Guid IdEspacoDeConhecimento { get; private set; }
    public Guid? IdProjeto { get; private set; }
    public TipoDeConhecimento Tipo { get; private set; }
    public string? Titulo => historico.Count == 0 ? null : historico[^1].Titulo;
    public IReadOnlyList<string> Tags => historico.Count == 0 ? [] : historico[^1].Tags ?? [];
    public string Conteudo { get; private set; } = "";
    public Sensibilidade Sensibilidade { get; private set; }
    public NaturezaDoConteudo Natureza { get; private set; }
    public ModoDeCaptura Modo { get; private set; }
    public string Justificativa { get; private set; } = "";
    public EstadoDoCandidato Estado { get; private set; }
    public string Impressao { get; private set; } = "";
    public Guid? IdConhecimento { get; private set; }
    public Guid? IdIncidente { get; private set; }
    public Guid? IdSolucao { get; private set; }
    public DateTimeOffset CriadoEm => historico[0].Instante;
    public int Revisao => historico.Count;
    public IReadOnlyList<AtoDoCandidato> Historico => historico.AsReadOnly();
    public ProvenienciaDoConhecimento Proveniencia => historico[^1].Proveniencia;

    public void Corrigir(int revisaoEsperada, TipoDeConhecimento tipo, string conteudo, Sensibilidade sensibilidade,
        string justificativa, ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante, string? titulo = null, IReadOnlyList<string>? tags = null)
    {
        ValidarMetadados(titulo, tags);
        GarantirPendente(revisaoEsperada, instante);
        Aplicar(tipo, conteudo, sensibilidade, justificativa, proveniencia, instante);
        Registrar("corrigido", proveniencia, instante, titulo ?? Titulo, tags ?? Tags);
    }
    public void RegistrarOrigemEquivalente(ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        GarantirPendente(Revisao, instante); ValidarEvidencia(proveniencia, instante);
        Registrar("origem equivalente", proveniencia, instante);
    }
    public Conhecimento Promover(int revisaoEsperada, ProvenienciaDoConhecimento responsavel, DateTimeOffset instante)
    {
        GarantirPendente(revisaoEsperada, instante); ValidarEvidencia(responsavel, instante);
        if (IdIncidente is not null && Tipo != TipoDeConhecimento.Aprendizado)
            throw new InvalidOperationException("Consolidação exige aprendizado classificado pelo usuário.");
        var conhecimento = new Conhecimento(IdEspacoDeConhecimento, IdProjeto, Tipo, Conteudo, Titulo is null ? null : JsonSerializer.Serialize(new { titulo = Titulo }),
            StatusDoConhecimento.Inferido, null, Sensibilidade, null, null, Tags, Proveniencia, instante);
        if (Tipo != TipoDeConhecimento.Inferencia) conhecimento.Confirmar(1, responsavel, instante);
        IdConhecimento = conhecimento.Id; Estado = EstadoDoCandidato.Promovido;
        Registrar("promovido", responsavel, instante);
        return conhecimento;
    }
    public void Rejeitar(int revisaoEsperada, ProvenienciaDoConhecimento responsavel, DateTimeOffset instante) =>
        Encerrar(revisaoEsperada, responsavel, instante, EstadoDoCandidato.Rejeitado, "rejeitado");
    public void Descartar(int revisaoEsperada, ProvenienciaDoConhecimento responsavel, DateTimeOffset instante) =>
        Encerrar(revisaoEsperada, responsavel, instante, EstadoDoCandidato.Descartado, "descartado");
    private void Encerrar(int revisao, ProvenienciaDoConhecimento p, DateTimeOffset instante, EstadoDoCandidato estado, string acao)
    {
        GarantirPendente(revisao, instante); ValidarEvidencia(p, instante);
        Estado = estado; Registrar(acao, p, instante);
    }
    private void GarantirPendente(int revisao, DateTimeOffset instante)
    {
        if (Estado != EstadoDoCandidato.Pendente || revisao != Revisao) throw new InvalidOperationException("Candidato encerrado ou revisão desatualizada.");
        if (instante < historico[^1].Instante) throw new ArgumentException("Timestamp anterior ao último ato.");
    }
    private static void ValidarEvidencia(ProvenienciaDoConhecimento p, DateTimeOffset instante)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (instante == default || p.ReferenciaDaFonte is null || p.TrechoDaFonte is null)
            throw new ArgumentException("Captura e decisões exigem referência e trecho de evidência.");
    }
    private void Aplicar(TipoDeConhecimento tipo, string conteudo, Sensibilidade sensibilidade, string justificativa,
        ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante)
    {
        ValidarEvidencia(proveniencia, instante);
        if (!Enum.IsDefined(tipo) || !Enum.IsDefined(sensibilidade) || string.IsNullOrWhiteSpace(conteudo) ||
            conteudo.Trim().Length > Conhecimento.TamanhoMaximoDoConteudo || string.IsNullOrWhiteSpace(justificativa) || justificativa.Trim().Length > 2000)
            throw new ArgumentException("Conteúdo, classificação ou justificativa inválida.");
        Tipo = tipo; Conteudo = conteudo.Trim(); Sensibilidade = sensibilidade; Justificativa = justificativa.Trim();
        var normalizado = Conteudo.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n");
        Impressao = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{(int)Tipo}|{(int)Sensibilidade}|{(int)Natureza}|{IdIncidente}|{IdSolucao}|{normalizado}")));
    }
    private static void ValidarMetadados(string? titulo, IReadOnlyList<string>? tags)
    {
        if (titulo is not null && (string.IsNullOrWhiteSpace(titulo) || titulo.Length > 300) ||
            tags is not null && (tags.Count > Conhecimento.QuantidadeMaximaDeTags || tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 100 || t.Any(char.IsControl))))
            throw new ArgumentException("Título ou tags inválidos.");
    }
    private void Registrar(string acao, ProvenienciaDoConhecimento proveniencia, DateTimeOffset instante,
        string? titulo = null, IReadOnlyList<string>? tags = null) =>
        historico.Add(new(Revisao + 1, acao, Tipo, Conteudo, Sensibilidade, Justificativa, proveniencia, instante, Estado, IdConhecimento,
            acao is "capturado" or "corrigido" ? titulo : Titulo, (acao is "capturado" or "corrigido" ? tags : Tags)?.ToArray()));
}
