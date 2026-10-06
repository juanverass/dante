namespace Dante.Application.ConstrucaoDeContexto;

public sealed record PacoteDeContextoDto(Guid Id, string TextoParaInjecao, IReadOnlyList<ItemDeContextoDto> Itens,
    IReadOnlyList<RegistroDeContextoDto> Registros, CustoDeContextoDto Custo, bool RecuperacaoLimitada)
{
    // O adapter só chama após envio bem-sucedido. Construção não equivale a injeção.
    public PacoteDeContextoDto RegistrarInjecao(IReadOnlyCollection<string> chaves)
    {
        var selecionadas=Itens.Select(x=>x.Chave).ToHashSet(StringComparer.Ordinal);
        if(chaves.Distinct(StringComparer.Ordinal).Count()!=chaves.Count || chaves.Any(x=>!selecionadas.Contains(x)))
            throw new ArgumentException("Injeção contém item não selecionado ou repetido.");
        return this with{Registros=Registros.Select(x=>chaves.Contains(x.Chave)?x with{Estado="injetado"}:x).ToArray()};
    }
}
