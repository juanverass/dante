using Dante.Application.SegurancaDoBrain;
using Dante.Application.BuscaDoBrain;
using Dante.Application.Comum;
using Dante.Application.Mapeamento;
using Dante.Application.Conhecimentos;
using Dante.Application.CapturaDeConhecimento;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.DocumentosFonte;
namespace Dante.Application.DocumentosFonte;

public sealed class DocumentoFonteAppService(IDocumentoFonteRepository fontes, IIndiceDeFontes indice,
    LeituraDoBrainAppService leitura, ICapturaDeConhecimentoAppService captura, IUnitOfWork unitOfWork,
    IMapsterTypeAdapter mapper, ILeitorDeFonteLocal leitor)
{
    public async Task<DocumentoFonteDto> ImportarAsync(AcessoAoBrain acesso, string origem, string formato, string conteudo,
        Sensibilidade sensibilidade = Sensibilidade.Pessoal, int? revisaoEsperada = null, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        if (sensibilidade == Sensibilidade.Confidencial && !acesso.PermitirConfidencial || sensibilidade == Sensibilidade.Secreto && !acesso.PermitirSecreto)
            throw new UnauthorizedAccessException("Classificação da fonte não autorizada.");
        ProtecaoDeSegredos.GarantirSeguro(origem, conteudo);
        if (formato is not ("markdown" or "texto")) throw new ArgumentException("Formato não suportado.");
        var fonte = await fontes.ObterPelaOrigemAsync(acesso, origem.Trim(), cancellationToken);
        if (fonte is null)
        {
            if (revisaoEsperada is not (null or 0)) throw new InvalidOperationException("Fonte não encontrada na revisão esperada.");
            fonte = new(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, origem, formato, conteudo, sensibilidade, acesso.IdUsuario, DateTimeOffset.UtcNow);
            await fontes.AdicionarAsync(fonte, cancellationToken);
        }
        else
        {
            if (fonte.Formato != formato) throw new ArgumentException("Formato da origem não pode ser alterado.");
            if (revisaoEsperada is null) throw new InvalidOperationException("Reimportação exige revisão esperada.");
            if (!fonte.Atualizar(revisaoEsperada.Value, conteudo, sensibilidade, acesso.IdUsuario, DateTimeOffset.UtcNow)) return ParaDto(fonte);
            fontes.Atualizar(fonte);
        }
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken); return ParaDto(fonte);
    }
    public async Task<DocumentoFonteDto> ImportarArquivoAsync(AcessoAoBrain acesso, string caminho, Sensibilidade sensibilidade,
        int? revisaoEsperada = null, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        var arquivo = await leitor.LerAsync(caminho, cancellationToken);
        return await ImportarAsync(acesso, arquivo.Origem, arquivo.Formato, arquivo.Conteudo, sensibilidade, revisaoEsperada, cancellationToken);
    }
    public async Task<IReadOnlyList<DocumentoFonteDto>> ListarAsync(AcessoAoBrain acesso, int limite = 100, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        if (limite is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limite));
        return (await fontes.ListarAsync(acesso, limite, cancellationToken)).Select(ParaDto).ToArray();
    }
    public async Task RemoverAsync(AcessoAoBrain acesso, Guid id, int revisao, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        var fonte = await fontes.ObterPorIdAsync(id, cancellationToken) ?? throw new ArgumentException("Fonte não encontrada.");
        if (fonte.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || fonte.IdProjeto != acesso.IdProjeto) throw new UnauthorizedAccessException();
        fonte.Remover(revisao, acesso.IdUsuario, DateTimeOffset.UtcNow); fontes.Atualizar(fonte);
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
    }
    public async Task<CandidatoDeConhecimentoDto> GerarCandidatoAsync(AcessoAoBrain acesso, Guid id, int revisao, int parte,
        TipoDeConhecimento tipo = TipoDeConhecimento.Nota, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        var trecho = await indice.ObterTrechoAsync(acesso, id, revisao, parte, cancellationToken) ?? throw new ArgumentException("Trecho não encontrado ou versão mudou.");
        var fonte = await fontes.ObterPorIdAsync(id, cancellationToken) ?? throw new ArgumentException("Fonte não encontrada.");
        return await captura.CapturarAsync(new() { IdEspacoDeConhecimento = acesso.IdEspacoDeConhecimento, IdProjeto = acesso.IdProjeto,
            Tipo = tipo, Conteudo = trecho.Conteudo, Sensibilidade = fonte.Sensibilidade, Natureza = NaturezaDoConteudo.FonteSelecionada,
            Modo = ModoDeCaptura.Explicita, Justificativa = "Trecho selecionado explicitamente; aguardando confirmação.",
            Proveniencia = new() { IdResponsavel = acesso.IdUsuario, Origem = "documento selecionado", ReferenciaDaFonte = trecho.Referencia,
                RevisaoDaFonte = trecho.Hash, TrechoDaFonte = trecho.Conteudo } }, cancellationToken);
    }
    public async Task ReconstruirAsync(AcessoAoBrain acesso, CancellationToken cancellationToken = default)
    { await leitura.ValidarAcessoAsync(acesso, cancellationToken); await indice.ReconstruirAsync(acesso, cancellationToken); }
    private DocumentoFonteDto ParaDto(DocumentoFonte fonte) => mapper.Mapear<DocumentoFonte, DocumentoFonteDto>(fonte);
}
