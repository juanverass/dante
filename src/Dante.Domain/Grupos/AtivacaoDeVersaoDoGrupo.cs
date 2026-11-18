using Dante.Domain.Comum;

namespace Dante.Domain.Grupos;

// Decisão humana que libera uma versão de grupo para execuções novas (#308, decisão F): quem decidiu, quando e o hash
// exato que foi aprovado. Imutável e só acrescentada; o grupo aponta para a ativação vigente. Ativar não autoriza nada
// além das capacidades já concedidas aos agentes.
public sealed class AtivacaoDeVersaoDoGrupo : EntidadeBase
{
    private AtivacaoDeVersaoDoGrupo()
    {
    }

    internal AtivacaoDeVersaoDoGrupo(VersaoDoGrupo versao, long idDoAutor, DateTimeOffset em)
    {
        IdGrupo = versao.IdGrupo;
        IdVersaoDoGrupo = versao.Id;
        NumeroDaVersao = versao.Numero;
        HashAprovado = versao.HashDaConfiguracao;
        IdDoAutor = idDoAutor;
        Em = em;
    }

    public Guid IdGrupo { get; private set; }
    public Guid IdVersaoDoGrupo { get; private set; }
    public int NumeroDaVersao { get; private set; }
    public string HashAprovado { get; private set; } = "";
    public long IdDoAutor { get; private set; }
    public DateTimeOffset Em { get; private set; }
}
