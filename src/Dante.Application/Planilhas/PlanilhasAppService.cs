namespace Dante.Application.Planilhas;

// Fachada genérica de planilhas (#224, AD-55). Conhece planilhas, abas, intervalos, células, fórmulas e mesclagens,
// nunca o domínio dos dados: interpretar "a linha da AWS" ou "o cliente X" é do agente, que trabalha
// progressivamente (metadados → abas → região pequena → busca → região relevante → escrita no alvo resolvido).
// Leitura só em planilha cadastrada; escrita sempre em coordenadas exatas, com valor anterior e auditoria; alvo
// ambíguo, valor esperado divergente, fórmula não autorizada e limpeza em massa recusam a escrita inteira.
public sealed class PlanilhasAppService(
    IPlanilhaService provedor,
    IConexaoDePlanilha conexao,
    ICadastroDePlanilhas cadastro,
    IAuditoriaDePlanilhas auditoria)
{
    internal const int LeituraPadrao = 500;
    internal const int LeituraMaxima = 5_000;
    internal const int OcorrenciasPadrao = 20;
    internal const int OcorrenciasMaximas = 100;
    internal const int MaximoDeCandidatos = 10;
    internal const int MaximoDeFormulasSobrescritas = 5;
    internal const int MaximoDeCelulasLimpas = 20;
    internal const int MaximoDeCelulasPorEscrita = 200;
    internal const int MaximoDeCaracteresPorCelula = 50_000;
    internal const int MaximoDeDeslocamento = 100;

    public Task<EstadoDaConexaoDto> ObterConexaoAsync(CancellationToken cancellationToken = default) =>
        conexao.ObterEstadoAsync(cancellationToken);

    public Task<AutorizacaoDeConexao> IniciarConexaoAsync(CancellationToken cancellationToken = default) =>
        conexao.IniciarAsync(cancellationToken);

    public Task<bool> DesconectarAsync(CancellationToken cancellationToken = default) =>
        conexao.DesconectarAsync(cancellationToken);

    public Task<IReadOnlyList<PlanilhaCadastradaDto>> ListarAsync(CancellationToken cancellationToken = default) =>
        cadastro.ListarAsync(cancellationToken);

    // Cadastrar confirma o acesso lendo os metadados: um ID errado ou sem permissão não entra no cadastro.
    public async Task<PlanilhaCadastradaDto> CadastrarAsync(string alias, string urlOuId, string? descricao = null,
        CancellationToken cancellationToken = default)
    {
        alias = PlanilhaValidator.ExigirAlias(alias);
        var id = provedor.IdentificarPlanilha(urlOuId ?? string.Empty)
                 ?? throw new ArgumentException("Informe a URL da planilha ou o ID dela.", nameof(urlOuId));
        descricao = PlanilhaValidator.ExigirDescricao(descricao);
        var existentes = await cadastro.ListarAsync(cancellationToken);
        if (existentes.FirstOrDefault(p => p.Alias == alias) is { } mesmoAlias && mesmoAlias.IdDaPlanilha != id)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Conflito,
                $"O alias {alias} já está cadastrado para outra planilha. Remova-o antes de reutilizar.");
        if (existentes.FirstOrDefault(p => p.IdDaPlanilha == id && p.Alias != alias) is { } mesmoId)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Conflito,
                $"Esta planilha já está cadastrada como {mesmoId.Alias}.");
        var metadados = await provedor.ObterMetadadosAsync(id, false, cancellationToken);
        var anterior = existentes.FirstOrDefault(p => p.Alias == alias);
        var planilha = new PlanilhaCadastradaDto
        {
            Alias = alias,
            IdDaPlanilha = id,
            Titulo = metadados.Titulo,
            Descricao = descricao ?? anterior?.Descricao,
            Regioes = anterior?.Regioes ?? [],
            CadastradaEm = anterior?.CadastradaEm ?? DateTimeOffset.UtcNow
        };
        await cadastro.SalvarAsync(planilha, cancellationToken);
        return planilha;
    }

    public Task<bool> RemoverAsync(string alias, CancellationToken cancellationToken = default) =>
        cadastro.RemoverAsync(PlanilhaValidator.ExigirAlias(alias), cancellationToken);

    public async Task<PlanilhaCadastradaDto> AnotarRegiaoAsync(string planilha, string nome, string intervalo,
        string? descricao = null, CancellationToken cancellationToken = default)
    {
        var regiao = PlanilhaValidator.ExigirRegiao(nome, intervalo, descricao);
        var cadastrada = await ResolverAsync(planilha, cancellationToken);
        var atualizada = cadastrada with
        {
            Regioes = cadastrada.Regioes.Where(r => !string.Equals(r.Nome, regiao.Nome, StringComparison.OrdinalIgnoreCase))
                .Append(new RegiaoConhecidaDto { Nome = regiao.Nome, Intervalo = regiao.Intervalo.ToString(), Descricao = regiao.Descricao })
                .OrderBy(r => r.Nome, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        await cadastro.SalvarAsync(atualizada, cancellationToken);
        return atualizada;
    }

    public async Task<PlanilhaCadastradaDto> RemoverRegiaoAsync(string planilha, string nome,
        CancellationToken cancellationToken = default)
    {
        var cadastrada = await ResolverAsync(planilha, cancellationToken);
        var restantes = cadastrada.Regioes.Where(r => !string.Equals(r.Nome, nome?.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (restantes.Length == cadastrada.Regioes.Count)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoEncontrada, $"A região {nome} não está anotada em {cadastrada.Alias}.");
        var atualizada = cadastrada with { Regioes = restantes };
        await cadastro.SalvarAsync(atualizada, cancellationToken);
        return atualizada;
    }

    public async Task<DescricaoDaPlanilhaDto> DescreverAsync(string planilha, CancellationToken cancellationToken = default)
    {
        var cadastrada = await ResolverAsync(planilha, cancellationToken);
        var metadados = await provedor.ObterMetadadosAsync(cadastrada.IdDaPlanilha, true, cancellationToken);
        return new DescricaoDaPlanilhaDto { Cadastro = cadastrada, Planilha = metadados };
    }

    // O intervalo pode ser A1 com aba ou o nome de uma região anotada no cadastro.
    public async Task<IntervaloDaPlanilhaDto> LerAsync(string planilha, string intervalo, int? limiteDeCelulas = null,
        CancellationToken cancellationToken = default)
    {
        var limite = PlanilhaValidator.ExigirLimite(limiteDeCelulas, LeituraPadrao, LeituraMaxima, nameof(limiteDeCelulas));
        var cadastrada = await ResolverAsync(planilha, cancellationToken);
        var regiao = cadastrada.Regioes.FirstOrDefault(r => string.Equals(r.Nome, intervalo?.Trim(), StringComparison.OrdinalIgnoreCase));
        var alvo = PlanilhaValidator.ExigirIntervaloComAba(regiao?.Intervalo ?? intervalo, nameof(intervalo));
        return await provedor.LerAsync(cadastrada.IdDaPlanilha, alvo, limite, cancellationToken);
    }

    // Sem aba nem intervalo, procura em todas as abas de grade da planilha.
    public async Task<ResultadoDaBuscaNaPlanilhaDto> BuscarAsync(string planilha, string termo, string? aba = null,
        string? intervalo = null, int? limite = null, CancellationToken cancellationToken = default)
    {
        termo = PlanilhaValidator.ExigirTermo(termo, nameof(termo));
        var maximo = PlanilhaValidator.ExigirLimite(limite, OcorrenciasPadrao, OcorrenciasMaximas, nameof(limite));
        var cadastrada = await ResolverAsync(planilha, cancellationToken);
        var (fontes, abas) = await LerParaBuscaAsync(cadastrada, aba, intervalo, cancellationToken);
        var (encontradas, truncado) = BuscaNaPlanilha.Buscar(fontes, termo, maximo);
        return new ResultadoDaBuscaNaPlanilhaDto
        {
            Termo = termo,
            AbasConsultadas = abas,
            Ocorrencias = encontradas.Select(e => e.Ocorrencia).ToArray(),
            Observacao = fontes.Select(f => f.Observacao).FirstOrDefault(o => o is not null),
            Truncado = truncado
        };
    }

    public async Task<ResultadoDaEscritaDto> AtualizarAsync(EscritaNaPlanilhaDto escrita, OrigemDaSolicitacao origem,
        CancellationToken cancellationToken = default)
    {
        var intervalos = PlanilhaValidator.ExigirEscrita(escrita);
        ArgumentNullException.ThrowIfNull(origem);
        var cadastrada = await ResolverAsync(escrita.Planilha, cancellationToken);
        return await EscreverAsync(cadastrada, escrita, intervalos, origem, "atualizar", cancellationToken);
    }

    public async Task<ResultadoDaEscritaDto> AtualizarPorReferenciaAsync(EscritaPorReferenciaDto escrita,
        OrigemDaSolicitacao origem, CancellationToken cancellationToken = default)
    {
        PlanilhaValidator.ExigirReferencia(escrita);
        ArgumentNullException.ThrowIfNull(origem);
        var cadastrada = await ResolverAsync(escrita.Planilha, cancellationToken);
        var (fontes, _) = await LerParaBuscaAsync(cadastrada, escrita.Aba, escrita.Intervalo, cancellationToken);
        var (encontradas, _) = BuscaNaPlanilha.Buscar(fontes, escrita.Referencia, int.MaxValue);
        var candidatos = BuscaNaPlanilha.Candidatos(encontradas);
        if (candidatos.Count == 0)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoEncontrada,
                $"Nenhuma célula corresponde a \"{escrita.Referencia}\". Nada foi alterado.");
        if (candidatos.Count > 1)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Ambigua,
                $"\"{escrita.Referencia}\" corresponde a {candidatos.Count} células. Nada foi alterado; confirme o alvo com o " +
                "usuário e escreva no endereço exato, ou restrinja a aba/intervalo.", candidatos.Take(MaximoDeCandidatos).ToArray());

        var referencia = candidatos[0];
        var linha = referencia.Celula.Linha + escrita.DeslocamentoDeLinhas;
        var coluna = escrita.Coluna is { } letra ? IntervaloA1.IndiceDaColuna(letra.Trim()) : referencia.Celula.Coluna + escrita.DeslocamentoDeColunas;
        if (linha < 1 || coluna < 1)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Invalida, "O deslocamento leva o alvo para fora da aba.");
        var alvo = IntervaloA1.DaCelula(referencia.Aba, linha, coluna);
        var dto = new EscritaNaPlanilhaDto
        {
            Planilha = escrita.Planilha,
            PermitirSobrescreverFormulas = escrita.PermitirSobrescreverFormulas,
            Alteracoes =
            [
                new AlteracaoDeIntervaloDto
                {
                    Intervalo = alvo.ToString(),
                    Valores = [[escrita.Valor]],
                    ValoresEsperados = escrita.ValorEsperado is null ? null : [[escrita.ValorEsperado]]
                }
            ]
        };
        return await EscreverAsync(cadastrada, dto, [alvo], origem, "atualizar_por_referencia", cancellationToken);
    }

    public async Task<ResultadoDaEscritaDto> AdicionarLinhaAsync(AdicaoDeLinhaDto adicao, OrigemDaSolicitacao origem,
        CancellationToken cancellationToken = default)
    {
        var tabela = PlanilhaValidator.ExigirLinha(adicao);
        ArgumentNullException.ThrowIfNull(origem);
        var cadastrada = await ResolverAsync(adicao.Planilha, cancellationToken);
        var escrito = await provedor.AdicionarLinhaAsync(cadastrada.IdDaPlanilha, tabela, adicao.Valores, cancellationToken);
        var celulas = adicao.Valores.Select((valor, i) => new CelulaAlteradaDto
        {
            Aba = escrito.Aba ?? tabela.Aba!,
            Endereco = IntervaloA1.Endereco(escrito.Linha, escrito.Coluna + i),
            ValorNovo = valor.ToString()
        }).ToArray();
        await AuditarAsync(cadastrada, celulas, "adicionar_linha", origem, cancellationToken);
        return new ResultadoDaEscritaDto { Planilha = cadastrada.Alias, Intervalos = [escrito.ToString()], Celulas = celulas };
    }

    private async Task<ResultadoDaEscritaDto> EscreverAsync(PlanilhaCadastradaDto cadastrada, EscritaNaPlanilhaDto escrita,
        IReadOnlyList<IntervaloA1> intervalos, OrigemDaSolicitacao origem, string operacao, CancellationToken cancellationToken)
    {
        var celulas = new List<CelulaAlteradaDto>();
        var divergencias = new List<string>();
        var formulas = new List<string>();
        var limpezas = 0;
        var revisoes = new string?[intervalos.Count];
        for (var a = 0; a < intervalos.Count; a++)
        {
            var intervalo = intervalos[a];
            var alteracao = escrita.Alteracoes[a];
            var atual = await provedor.LerAsync(cadastrada.IdDaPlanilha, intervalo, (int)intervalo.QuantidadeDeCelulas, cancellationToken);
            revisoes[a] = atual.Revisao;
            var porEndereco = atual.Celulas.ToDictionary(c => (c.Linha, c.Coluna));
            var mesclagens = atual.Mesclagens.Select(m => IntervaloA1.Interpretar(m)).ToArray();
            for (var i = 0; i < intervalo.QuantidadeDeLinhas; i++)
            for (var j = 0; j < intervalo.QuantidadeDeColunas; j++)
            {
                var (linha, coluna) = (intervalo.Linha + i, intervalo.Coluna + j);
                var endereco = IntervaloA1.Endereco(linha, coluna);
                if (mesclagens.FirstOrDefault(m => m.Contem(linha, coluna) && (m.Linha, m.Coluna) != (linha, coluna)) is { } mesclagem)
                    throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Invalida,
                        $"{endereco} faz parte da mesclagem {mesclagem.Celulas}; o valor vive em {IntervaloA1.Endereco(mesclagem.Linha, mesclagem.Coluna)}. Nada foi alterado.");
                porEndereco.TryGetValue((linha, coluna), out var anterior);
                var novo = alteracao.Valores[i][j] ?? ValorDeCelula.Vazio;
                if (alteracao.ValoresEsperados?[i][j] is { } esperado && !Corresponde(anterior, esperado))
                    divergencias.Add($"{endereco}: esperado \"{esperado}\", atual \"{anterior?.ValorExibido ?? string.Empty}\"");
                if (anterior?.Formula is { } formula) formulas.Add($"{endereco} ({formula})");
                if (novo.EstaVazio && anterior is { EstaVazia: false }) limpezas++;
                celulas.Add(new CelulaAlteradaDto
                {
                    Aba = intervalo.Aba!,
                    Endereco = endereco,
                    ValorAnterior = anterior?.ValorExibido,
                    FormulaAnterior = anterior?.Formula,
                    ValorNovo = novo.ToString()
                });
            }
        }

        if (divergencias.Count > 0)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Conflito,
                "A planilha mudou ou o alvo não é o esperado; nada foi alterado. " + string.Join("; ", divergencias));
        if (formulas.Count > 0 && !escrita.PermitirSobrescreverFormulas)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Conflito,
                $"O alvo contém fórmula: {string.Join(", ", formulas)}. Nada foi alterado; confirme com o usuário antes de sobrescrever.");
        if (formulas.Count > MaximoDeFormulasSobrescritas)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada,
                $"Sobrescrever {formulas.Count} fórmulas de uma vez não é suportado (máximo {MaximoDeFormulasSobrescritas}).");
        if (limpezas > MaximoDeCelulasLimpas)
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.NaoSuportada,
                $"Limpar {limpezas} células preenchidas de uma vez não é suportado (máximo {MaximoDeCelulasLimpas}).");

        var escritas = intervalos.Select((intervalo, a) => new ValoresParaEscrita(intervalo, escrita.Alteracoes[a].Valores,
            revisoes[a])).ToArray();
        var atualizados = await provedor.AtualizarAsync(cadastrada.IdDaPlanilha, escritas, cancellationToken);
        await AuditarAsync(cadastrada, celulas, operacao, origem, cancellationToken);
        return new ResultadoDaEscritaDto { Planilha = cadastrada.Alias, Intervalos = atualizados, Celulas = celulas };
    }

    private async Task AuditarAsync(PlanilhaCadastradaDto cadastrada, IReadOnlyList<CelulaAlteradaDto> celulas, string operacao,
        OrigemDaSolicitacao origem, CancellationToken cancellationToken)
    {
        var conta = (await conexao.ObterEstadoAsync(cancellationToken)).Conta;
        var instante = DateTimeOffset.UtcNow;
        foreach (var celula in celulas)
            await auditoria.RegistrarAsync(new RegistroDeAuditoriaDePlanilha(instante, conta, cadastrada.IdDaPlanilha,
                cadastrada.Alias, celula.Aba, operacao, celula.Endereco, celula.ValorAnterior, celula.FormulaAnterior,
                celula.ValorNovo, origem.ToString(), origem.Agente), cancellationToken);
    }

    // O valor esperado é comparado com o exibido (o que o agente leu) ou com o bruto, sem espaços nas pontas.
    private static bool Corresponde(CelulaDaPlanilhaDto? atual, string esperado)
    {
        var alvo = esperado.Trim();
        return string.Equals((atual?.ValorExibido ?? string.Empty).Trim(), alvo, StringComparison.Ordinal) ||
               atual?.ValorBruto is { } bruto && string.Equals(bruto.ToString(), alvo, StringComparison.Ordinal);
    }

    private async Task<(IReadOnlyList<ValoresLidos> Fontes, IReadOnlyList<string> Abas)> LerParaBuscaAsync(
        PlanilhaCadastradaDto cadastrada, string? aba, string? intervalo, CancellationToken cancellationToken)
    {
        IReadOnlyList<IntervaloA1> alvos;
        if (intervalo is not null)
        {
            var alvo = IntervaloA1.TentarInterpretar(intervalo, out var interpretado) && interpretado!.Aba is null && aba is not null
                ? interpretado.NaAba(aba)
                : PlanilhaValidator.ExigirIntervaloComAba(intervalo, nameof(intervalo));
            alvos = [alvo];
        }
        else if (!string.IsNullOrWhiteSpace(aba))
        {
            alvos = [IntervaloA1.DaAba(aba.Trim())];
        }
        else
        {
            var metadados = await provedor.ObterMetadadosAsync(cadastrada.IdDaPlanilha, false, cancellationToken);
            alvos = metadados.Abas.Where(a => a.Tipo == "GRID").Select(a => IntervaloA1.DaAba(a.Titulo)).ToArray();
        }
        var fontes = alvos.Count == 0 ? [] : await provedor.LerValoresExibidosAsync(cadastrada.IdDaPlanilha, alvos, cancellationToken);
        return (fontes, alvos.Select(a => a.Aba!).Distinct().ToArray());
    }

    // Alias cadastrado, ou ID/URL de uma planilha cadastrada; nada fora do cadastro é lido ou escrito.
    private async Task<PlanilhaCadastradaDto> ResolverAsync(string planilha, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(planilha))
            throw new ArgumentException("Informe o alias da planilha.", nameof(planilha));
        if (PlanilhaValidator.PareceAlias(planilha) &&
            await cadastro.ObterAsync(PlanilhaValidator.ExigirAlias(planilha), cancellationToken) is { } porAlias)
            return porAlias;
        return provedor.IdentificarPlanilha(planilha.Trim()) is { } id &&
               (await cadastro.ListarAsync(cancellationToken)).FirstOrDefault(p => p.IdDaPlanilha == id) is { } porId
            ? porId
            : throw NaoCadastrada(planilha);
    }

    private static FalhaDePlanilhaException NaoCadastrada(string planilha) => new(MotivoDaFalhaDePlanilha.NaoCadastrada,
        $"A planilha {planilha.Trim()} não está cadastrada. Cadastre-a pela URL ou ID com um alias antes de usá-la.");
}
