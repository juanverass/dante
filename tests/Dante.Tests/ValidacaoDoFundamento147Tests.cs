using Dante.Application.DocumentosFonte;

namespace Dante.Tests;

// Regressão determinística da verificação b_origem_informada da validação funcional do fundamento (PR #293): sem CLI e
// sem banco. A resposta B só passa se cada conhecimento trouxer a própria referência de trecho completa, depois da
// própria linha de conteúdo; o identificador da fonte, comum aos quatro, nunca substitui as referências individuais.
public sealed class ValidacaoDoFundamento147Tests
{
    private static readonly Guid IdDaFonte = Guid.Parse("91acd6cc-b210-435c-96d7-201ab225555a");
    private const string HashDoDocumento = "6C857A4F0053D04596B561D61591E2710F67F418F0C33C7D57FF4B88831F0D55";

    private static readonly (string Papel, string Linha, int Inicio, int Fim)[] Secoes =
    [
        ("Incidente", "O Worker publicado em 01/11 não oferecia o campo fundamento do Brain.", 0, 154),
        ("Solução", "Validar o contrato da PR #292 em harness controlado com PostgreSQL descartável.", 154, 245),
        ("Restrição", "O banco canônico e o Worker em execução não podem ser alterados.", 245, 361),
        ("Próximo passo", "Pré-registrar o próximo experimento da Issue #147 somente após esta validação.", 361, 494)
    ];

    private static readonly (string Linha, string Referencia)[] Itens = Secoes
        .Select(s => (s.Linha, new ReferenciaDeTrecho(IdDaFonte, 1, 0, HashDoDocumento, s.Inicio, s.Fim, ReferenciaDeTrecho.HashDoTexto(s.Linha)).Texto)).ToArray();

    private static string Resposta(Func<int, string> origem) =>
        $"## Fonte bruta\n- `fonte:{IdDaFonte:D}@1`, citada pelos quatro conhecimentos abaixo.\n\n" +
        string.Concat(Secoes.Select((s, i) => $"**{s.Papel}**\n> {s.Linha}\n\nOrigem: `{origem(i)}`\n\n")) + "## Relações\n- ResolvidoPor\n";

    [Fact]
    public void ReferenciaCompletaDeCadaConhecimentoAposASuaLinhaAprova()
    {
        Assert.Equal(4, Itens.Select(i => i.Referencia).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(Issue147BrainIndependenteTests.OrigensNaoInformadas(Resposta(i => Itens[i].Referencia), Itens));
    }

    // O falso positivo da revisão: o ID da fonte aparece uma vez e as referências individuais não aparecem.
    [Fact]
    public void IdentificadorDaFonteSemReferenciasIndividuaisReprova()
    {
        var resposta = Resposta(_ => $"fonte:{IdDaFonte:D}@1");
        Assert.Contains($"fonte:{IdDaFonte:D}", resposta, StringComparison.Ordinal);
        Assert.Equal(Itens.Select(i => i.Referencia), Issue147BrainIndependenteTests.OrigensNaoInformadas(resposta, Itens));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void IdentificadorDaFonteComUmaReferenciaOmitidaReprovaSoEla(int omitida)
    {
        var resposta = Resposta(i => i == omitida ? $"fonte:{IdDaFonte:D}@1" : Itens[i].Referencia);
        Assert.Equal([Itens[omitida].Referencia], Issue147BrainIndependenteTests.OrigensNaoInformadas(resposta, Itens));
    }

    // Formato observado na execução real de c442f8d: hash do documento abreviado com reticências.
    [Fact]
    public void ReferenciaAbreviadaReprova()
    {
        var resposta = Resposta(i => Itens[i].Referencia.Replace(HashDoDocumento, "6C857A4F…0D55", StringComparison.Ordinal));
        Assert.Equal(Itens.Select(i => i.Referencia), Issue147BrainIndependenteTests.OrigensNaoInformadas(resposta, Itens));
    }

    [Fact]
    public void ReferenciasTrocadasEntreConhecimentosReprovam()
    {
        var resposta = Resposta(i => Itens[i switch { 0 => 1, 1 => 0, _ => i }].Referencia);
        Assert.Equal([Itens[0].Referencia, Itens[1].Referencia], Issue147BrainIndependenteTests.OrigensNaoInformadas(resposta, Itens));
    }

    // Referências listadas todas juntas no fim ficam associadas só ao último conhecimento.
    [Fact]
    public void ReferenciasSoNoFimSemAssociacaoReprovam()
    {
        var resposta = Resposta(_ => "ver lista abaixo") + string.Join("\n", Itens.Select(i => i.Referencia));
        Assert.Equal(Itens.Take(3).Select(i => i.Referencia), Issue147BrainIndependenteTests.OrigensNaoInformadas(resposta, Itens));
    }

    [Fact]
    public void ReferenciaRepetidaEntreConhecimentosReprovaTodos()
    {
        var repetidos = Itens.Select(i => (i.Linha, Itens[0].Referencia)).ToArray();
        Assert.Equal(4, Issue147BrainIndependenteTests.OrigensNaoInformadas(Resposta(_ => Itens[0].Referencia), repetidos).Length);
    }
}
