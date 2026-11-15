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
    // A autoria é declarada por quem chama: o conteúdo pertence a quem o escreveu, não a quem pediu a importação (AD-59 P2).
    public async Task<DocumentoFonteDto> ImportarAsync(AcessoAoBrain acesso, string origem, string formato, string conteudo, AutoriaDaFonte autoria,
        Sensibilidade sensibilidade = Sensibilidade.Pessoal, int? revisaoEsperada = null, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        if (sensibilidade == Sensibilidade.Confidencial && !acesso.PermitirConfidencial || sensibilidade == Sensibilidade.Secreto && !acesso.PermitirSecreto)
            throw new UnauthorizedAccessException("Classificação da fonte não autorizada.");
        ProtecaoDeSegredos.GarantirSeguro(origem, conteudo);
        DocumentoFonteValidator.ValidarFormato(formato);
        var fonte = await fontes.ObterPelaOrigemAsync(acesso, origem.Trim(), cancellationToken);
        if (fonte is null)
        {
            if (revisaoEsperada is not (null or 0)) throw new InvalidOperationException("Fonte não encontrada na revisão esperada.");
            fonte = new(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, origem, formato, conteudo, autoria, sensibilidade, acesso.IdUsuario, DateTimeOffset.UtcNow);
            await fontes.AdicionarAsync(fonte, cancellationToken);
        }
        else
        {
            if (fonte.Formato != formato) throw new ArgumentException("Formato da origem não pode ser alterado.");
            if (revisaoEsperada is null) throw new InvalidOperationException("Reimportação exige revisão esperada.");
            if (!fonte.Atualizar(revisaoEsperada.Value, conteudo, autoria, sensibilidade, acesso.IdUsuario, DateTimeOffset.UtcNow)) return ParaDto(fonte);
            fontes.Atualizar(fonte);
        }
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken); return ParaDto(fonte);
    }
    public const int TamanhoMaximoDaEvidencia = 20_000;
    public const int LimiteDePossiveisDuplicidades = 10;
    // Evidência do agente (#257): sempre nova fonte bruta com autoria Agente; nunca cria candidato ou conhecimento.
    // Mesmo conteúdo repetido gera outro registro, preservando proveniência e metadados de cada ocorrência.
    public async Task<EvidenciaRegistradaDto> RegistrarEvidenciaAsync(AcessoAoBrain acesso, RegistroDeEvidenciaDto evidencia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidencia);
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        if (!SensibilidadeDeAgente(evidencia.Sensibilidade)) throw new ArgumentException("Agentes registram evidências somente como Publico, Pessoal ou Trabalho.");
        if (string.IsNullOrWhiteSpace(evidencia.Conteudo) || evidencia.Conteudo.Length > TamanhoMaximoDaEvidencia)
            throw new ArgumentException($"Evidência deve ter conteúdo de até {TamanhoMaximoDaEvidencia} caracteres.");
        if (string.IsNullOrWhiteSpace(evidencia.Proveniencia)) throw new ArgumentException("Proveniência da evidência obrigatória.");
        string?[] textos = [evidencia.Titulo, evidencia.Conteudo, evidencia.Justificativa, evidencia.Proveniencia, .. evidencia.Tags ?? []];
        ProtecaoDeSegredos.GarantirSeguro(textos);
        ProtecaoDeConteudoOperacional.Garantir(textos, "Evidência aceita somente resultados operacionais selecionados.");
        var fonte = new DocumentoFonte(acesso.IdEspacoDeConhecimento, acesso.IdProjeto, $"evidencia:{Guid.NewGuid():D}", "markdown",
            evidencia.Conteudo, AutoriaDaFonte.Agente, evidencia.Sensibilidade, acesso.IdUsuario, DateTimeOffset.UtcNow,
            evidencia.Titulo, evidencia.Tags, evidencia.Justificativa, evidencia.Proveniencia);
        // Só referências que o agente já poderia ler; itens protegidos não são revelados nem ocupam o limite.
        var iguais = (await fontes.ListarPorHashAsync(acesso, fonte.Hash, SensibilidadesDeAgente, LimiteDePossiveisDuplicidades, cancellationToken))
            .Select(x => ParaDto(x).Referencia).ToArray();
        await fontes.AdicionarAsync(fonte, cancellationToken);
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return new(ParaDto(fonte), iguais);
    }
    private static readonly Sensibilidade[] SensibilidadesDeAgente = [Sensibilidade.Publico, Sensibilidade.Pessoal, Sensibilidade.Trabalho];
    private static bool SensibilidadeDeAgente(Sensibilidade s) => SensibilidadesDeAgente.Contains(s);
    public async Task<DocumentoFonteDto> ImportarArquivoAsync(AcessoAoBrain acesso, string caminho, Sensibilidade sensibilidade,
        int? revisaoEsperada = null, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        var arquivo = await leitor.LerAsync(caminho, cancellationToken);
        // Arquivo local indicado pelo operador na CLI: importação do próprio usuário.
        return await ImportarAsync(acesso, arquivo.Origem, arquivo.Formato, arquivo.Conteudo, AutoriaDaFonte.Usuario, sensibilidade, revisaoEsperada, cancellationToken);
    }
    public async Task<IReadOnlyList<DocumentoFonteDto>> ListarAsync(AcessoAoBrain acesso, int limite = 100, CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        ValidacaoDeEntrada.ExigirFaixa(limite, 1, 100, nameof(limite));
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
        // AD-59 I9: a natureza segue quem escreveu a fonte. Autoria desconhecida não origina candidato,
        // portanto nunca chega a conhecimento confirmado (D3).
        var natureza = fonte.Autoria switch
        {
            AutoriaDaFonte.Usuario => NaturezaDoConteudo.FonteSelecionada,
            AutoriaDaFonte.Agente => NaturezaDoConteudo.ConclusaoDoAgente,
            _ => throw new InvalidOperationException("Fonte sem autoria registrada; importe-a novamente para gerar candidato.")
        };
        return await captura.CapturarAsync(new() { IdEspacoDeConhecimento = acesso.IdEspacoDeConhecimento, IdProjeto = acesso.IdProjeto,
            Tipo = tipo, Conteudo = trecho.Conteudo, Sensibilidade = fonte.Sensibilidade, Natureza = natureza,
            Modo = ModoDeCaptura.Explicita, Justificativa = "Trecho selecionado explicitamente; aguardando confirmação.",
            Proveniencia = new() { IdResponsavel = acesso.IdUsuario, Origem = $"documento selecionado;autoria-da-fonte:{fonte.Autoria}", ReferenciaDaFonte = trecho.Referencia,
                RevisaoDaFonte = trecho.Hash, TrechoDaFonte = trecho.Conteudo } }, cancellationToken);
    }
    // Proveniência por trecho na captura (#147). O servidor, não quem captura, localiza a citação na revisão vigente e grava
    // referência, intervalo e hash: a citação são linhas inteiras, ocorre uma única vez, cabe numa parte indexada e está
    // contida literalmente no conteúdo capturado (comparação normalizada, a mesma da prova de consolidação). Fonte fora do
    // escopo, removida, protegida ou acima da classificação permitida responde como inexistente.
    public const string FundamentoInvalido = "A citação precisa ser linhas inteiras, únicas na revisão atual da fonte, dentro de um trecho indexado, e estar literalmente no conteúdo.";
    public async Task<FundamentoNaFonteDto> FundamentarAsync(AcessoAoBrain acesso, Guid id, int revisao, string citacao, string conteudo,
        CancellationToken cancellationToken = default)
    {
        await leitura.ValidarAcessoAsync(acesso, cancellationToken);
        var fonte = await fontes.ObterPorIdAsync(id, cancellationToken);
        if (fonte is null || fonte.Removido || fonte.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || fonte.IdProjeto != acesso.IdProjeto ||
            fonte.Sensibilidade == Sensibilidade.Secreto && !acesso.PermitirSecreto || fonte.Sensibilidade == Sensibilidade.Confidencial && !acesso.PermitirConfidencial)
            throw new KeyNotFoundException("Fonte não encontrada.");
        if (fonte.Revisao != revisao) throw new InvalidOperationException("Revisão da fonte desatualizada.");
        var texto = fonte.Conteudo;
        if (string.IsNullOrWhiteSpace(citacao)) throw new ArgumentException(FundamentoInvalido);
        var posicao = texto.IndexOf(citacao, StringComparison.Ordinal);
        if (posicao < 0 || texto.IndexOf(citacao, posicao + 1, StringComparison.Ordinal) >= 0 || char.IsLowSurrogate(texto[posicao]))
            throw new ArgumentException(FundamentoInvalido);
        var fim = posicao + citacao.Length;
        // A citação termina em fim de linha; o terminador entra no intervalo para que a próxima linha comece onde esta acaba.
        if (fim < texto.Length && texto[fim - 1] != '\n')
        {
            if (texto[fim] == '\n') fim++;
            else if (texto[fim] == '\r' && fim + 1 < texto.Length && texto[fim + 1] == '\n') fim += 2;
            else throw new ArgumentException(FundamentoInvalido);
        }
        if (posicao > 0 && texto[posicao - 1] != '\n') throw new ArgumentException(FundamentoInvalido);
        var runas = texto.EnumerateRunes().ToArray();
        var a = texto[..posicao].EnumerateRunes().Count(); var b = a + texto[posicao..fim].EnumerateRunes().Count();
        var citado = texto[posicao..fim];
        if (!ConstrucaoDeContexto.ConsolidacaoDeFontes.EmFronteiraDeLinha(runas, a, b) ||
            !ConstrucaoDeContexto.SelecaoDeContexto.Normalizar(conteudo).Contains(ConstrucaoDeContexto.SelecaoDeContexto.Normalizar(citado), StringComparison.Ordinal))
            throw new ArgumentException(FundamentoInvalido);
        for (var numero = 0; numero < ConstrucaoDeContexto.ConsolidacaoDeFontes.MaximoDeTrechos; numero++)
        {
            var parte = await indice.ObterTrechoAsync(acesso, id, revisao, numero, cancellationToken);
            if (parte is null) break;
            var tamanho = parte.Conteudo.EnumerateRunes().Count();
            if (parte.Inicio > a || b > parte.Inicio + tamanho) continue;
            // A parte precisa ser o próprio documento naquele intervalo; índice divergente não fundamenta nada.
            if (string.Concat(parte.Conteudo.EnumerateRunes().Skip(a - parte.Inicio).Take(b - a)) != citado) break;
            var referencia = new ReferenciaDeTrecho(id, revisao, parte.Numero, parte.Hash, a, b, ReferenciaDeTrecho.HashDoTexto(citado));
            return new(referencia.Texto, parte.Hash, citado);
        }
        throw new ArgumentException(FundamentoInvalido);
    }
    public async Task ReconstruirAsync(AcessoAoBrain acesso, CancellationToken cancellationToken = default)
    { await leitura.ValidarAcessoAsync(acesso, cancellationToken); await indice.ReconstruirAsync(acesso, cancellationToken); }
    private DocumentoFonteDto ParaDto(DocumentoFonte fonte) => mapper.Mapear<DocumentoFonte, DocumentoFonteDto>(fonte);
}
