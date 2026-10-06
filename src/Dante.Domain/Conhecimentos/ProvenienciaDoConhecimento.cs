namespace Dante.Domain.Conhecimentos;

// Referências lógicas a evidências; nenhuma leitura de fonte ocorre no domínio.
public sealed record ProvenienciaDoConhecimento
{
    public ProvenienciaDoConhecimento(Guid idResponsavel, string origem, string? referenciaDaFonte = null,
        string? revisaoDaFonte = null, string? trechoDaFonte = null)
    {
        if (idResponsavel == Guid.Empty) throw new ArgumentException("Responsável é obrigatório.", nameof(idResponsavel));
        if (string.IsNullOrWhiteSpace(origem)) throw new ArgumentException("Origem é obrigatória.", nameof(origem));
        IdResponsavel = idResponsavel;
        Origem = TextoValido(origem, 200)!;
        ReferenciaDaFonte = TextoValido(referenciaDaFonte, 2000);
        RevisaoDaFonte = TextoValido(revisaoDaFonte, 200);
        TrechoDaFonte = TextoValido(trechoDaFonte, 10000, aparar: false);
        if (ReferenciaDaFonte is null && (RevisaoDaFonte is not null || TrechoDaFonte is not null))
            throw new ArgumentException("Revisão e trecho exigem referência à fonte.");
    }

    public Guid IdResponsavel { get; }
    public string Origem { get; }
    public string? ReferenciaDaFonte { get; }
    public string? RevisaoDaFonte { get; }
    public string? TrechoDaFonte { get; }

    private static string? TextoValido(string? texto, int limite, bool aparar = true)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        if (aparar) texto = texto.Trim();
        if (texto.Length > limite) throw new ArgumentException("Referência de proveniência excede o limite.");
        return texto;
    }
}
