using Dante.Domain.Comum;

namespace Dante.Domain.Grupos;

// Linha persistida de um participante de uma versão de grupo (AD-69). Imutável como a versão: o banco recusa UPDATE e
// DELETE. O papel é único dentro da versão.
public sealed class ParticipanteDaVersaoDoGrupo : EntidadeBase
{
    private ParticipanteDaVersaoDoGrupo()
    {
    }

    internal ParticipanteDaVersaoDoGrupo(Guid idVersaoDoGrupo, ParticipanteDoGrupo participante)
    {
        IdVersaoDoGrupo = idVersaoDoGrupo;
        IdAgente = participante.IdAgente;
        Papel = participante.Papel;
        Responsabilidades = participante.Responsabilidades;
        PodeEscrever = participante.PodeEscrever;
    }

    public Guid IdVersaoDoGrupo { get; private set; }
    public Guid IdAgente { get; private set; }
    public string Papel { get; private set; } = "";
    public string? Responsabilidades { get; private set; }
    public bool PodeEscrever { get; private set; }

    public ParticipanteDoGrupo Participante => new(IdAgente, Papel, Responsabilidades, PodeEscrever);
}
